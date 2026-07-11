"""도형 프레임워크 — facing 상대 좌표 정의 + 월드 절대 좌표 변환.

핵심 원칙 (사용자 요구):
  - 모든 패턴 기하는 보스 facing 기준 **상대 좌표**(RelShape)로 정의한다.
  - 보스 위치(origin) + 락된 facing 으로 **월드 절대 좌표**(Shape)로 bake 한다.
  - Unity 는 bake 된 월드 Shape.to_dict() 를 그대로 그린다 → "몸 방향과 장판 방향 불일치" 원천 차단.

상대 좌표계 (facing frame):
  forward = facing 방향(+x_local), lateral = 왼쪽(+y_local, forward 기준 반시계 90°)
  world = origin + forward*(cos f, sin f) + lateral*(-sin f, cos f)

Shape 종류: circle / fan / line / donut
"""
from __future__ import annotations
from dataclasses import dataclass, field
from typing import Dict, List, Tuple
import math

Pos = Tuple[float, float]


# ─────────────────── 기하 유틸 ───────────────────

def _clamp(v: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, v))


def _dist(a: Pos, b: Pos) -> float:
    return math.hypot(a[0] - b[0], a[1] - b[1])


def _rel_to_world(fwd: float, lat: float, ox: float, oy: float, facing: float) -> Pos:
    c = math.cos(facing); s = math.sin(facing)
    wx = ox + fwd * c - lat * s
    wy = oy + fwd * s + lat * c
    return (wx, wy)


def _point_in_circle(p: Pos, center: Pos, radius: float) -> bool:
    return _dist(p, center) <= radius


def _point_in_donut(p: Pos, center: Pos, r_in: float, r_out: float) -> bool:
    d = _dist(p, center)
    return r_in <= d <= r_out


def _point_in_fan(p: Pos, origin: Pos, forward_rad: float,
                  full_angle_rad: float, radius: float) -> bool:
    dx = p[0] - origin[0]; dy = p[1] - origin[1]
    d2 = dx * dx + dy * dy
    if d2 > radius * radius:
        return False
    if d2 < 1e-8:
        return True
    ang = math.atan2(dy, dx)
    diff = abs(ang - forward_rad)
    if diff > math.pi:
        diff = 2 * math.pi - diff
    return diff <= full_angle_rad * 0.5


def _point_in_line_segment(p: Pos, a: Pos, b: Pos, half_width: float) -> bool:
    ax, ay = a; bx, by = b
    dx = bx - ax; dy = by - ay
    seg_len2 = dx * dx + dy * dy
    if seg_len2 < 1e-8:
        return _dist(p, a) <= half_width
    t = ((p[0] - ax) * dx + (p[1] - ay) * dy) / seg_len2
    t = _clamp(t, 0.0, 1.0)
    px = ax + t * dx; py = ay + t * dy
    return math.hypot(p[0] - px, p[1] - py) <= half_width


def segment_intersects_circle(a: Pos, b: Pos, center: Pos, radius: float) -> bool:
    """선분 a-b 가 원(center, radius)과 교차/접하는지. LOS 은신 판정에 사용."""
    return _point_in_line_segment(center, a, b, radius)


# ─────────────────── 월드 절대 Shape (Unity 송신용) ───────────────────

@dataclass
class Shape:
    """월드 절대 좌표 위험 영역 — Unity 렌더 + contains() 판정.

    kind별 params:
      circle : cx, cy, r
      donut  : cx, cy, r_in, r_out
      fan    : cx, cy, angle(월드 forward rad), width(full angle rad), r
      line   : ax, ay, bx, by, hw(half width)
    """
    kind: str
    params: Dict[str, float] = field(default_factory=dict)

    def to_dict(self) -> dict:
        return {"kind": self.kind, **self.params}

    def contains(self, p: Pos) -> bool:
        return _shape_contains(self, p)


def _shape_contains(s: Shape, p: Pos) -> bool:
    k = s.kind
    q = s.params
    if k == "circle":
        return _point_in_circle(p, (q["cx"], q["cy"]), q["r"])
    if k == "donut":
        return _point_in_donut(p, (q["cx"], q["cy"]), q["r_in"], q["r_out"])
    if k == "fan":
        return _point_in_fan(p, (q["cx"], q["cy"]), q["angle"], q["width"], q["r"])
    if k == "line":
        return _point_in_line_segment(p, (q["ax"], q["ay"]), (q["bx"], q["by"]), q["hw"])
    return False


# ─────────────────── facing 상대 RelShape ───────────────────

@dataclass
class RelShape:
    """facing 상대 좌표 도형 스펙. bake() 로 월드 Shape 생성.

    kind별 params (상대 좌표 — forward/lateral frame):
      circle : fwd, lat, r          (보스 origin 기준 상대 중심)
      donut  : r_in, r_out          (origin 중심)
      fan    : angle_rel, width, r  (angle_rel: facing 기준 각 오프셋, width: full angle)
      line   : angle_rel, hw, length, start   (origin 에서 angle_rel 방향 직선)
    world=True 이면 params 가 이미 월드 절대 좌표 (circle/donut 만).
    """
    kind: str
    params: Dict[str, float] = field(default_factory=dict)
    world: bool = False

    def bake(self, ox: float, oy: float, facing: float) -> Shape:
        k = self.kind
        q = self.params
        if self.world:
            return Shape(k, dict(q))
        if k == "circle":
            wx, wy = _rel_to_world(q["fwd"], q["lat"], ox, oy, facing)
            return Shape("circle", {"cx": wx, "cy": wy, "r": q["r"]})
        if k == "donut":
            return Shape("donut", {"cx": ox, "cy": oy, "r_in": q["r_in"], "r_out": q["r_out"]})
        if k == "fan":
            return Shape("fan", {
                "cx": ox, "cy": oy,
                "angle": facing + q["angle_rel"],
                "width": q["width"],
                "r": q["r"],
            })
        if k == "line":
            ang = facing + q["angle_rel"]
            start = q.get("start", 0.0)
            length = q["length"]
            ax = ox + math.cos(ang) * start
            ay = oy + math.sin(ang) * start
            bx = ox + math.cos(ang) * length
            by = oy + math.sin(ang) * length
            return Shape("line", {"ax": ax, "ay": ay, "bx": bx, "by": by, "hw": q["hw"]})
        raise ValueError(f"unknown RelShape kind: {k}")


def bake_shapes(rel_shapes: List[RelShape], ox: float, oy: float, facing: float) -> List[Shape]:
    return [rs.bake(ox, oy, facing) for rs in rel_shapes]


# ─────────────────── 8방향 위험 센서 (관찰용) ───────────────────

def sample_danger_sensor(pos: Pos, world_shapes: List[Shape],
                         max_distance: float = 6.0, step: float = 0.4) -> List[float]:
    """8방향으로 나아가며 가장 가까운 위험까지 거리 측정. 반환 8개 [0,1] (1=안전)."""
    result: List[float] = []
    for i in range(8):
        theta = i * math.pi / 4
        dx = math.cos(theta); dy = math.sin(theta)
        found = max_distance
        t = step
        while t <= max_distance:
            probe = (pos[0] + dx * t, pos[1] + dy * t)
            if any(s.contains(probe) for s in world_shapes):
                found = t
                break
            t += step
        result.append(found / max_distance)
    return result
