"""RaidEnv 패키지 — '혈월의 마수 군주' 로스트아크식 보스 레이드 (신규 v2).

로스트아크 발탄 모티프. facing 기반 패턴 기하 + PatternStep 시퀀스 프레임워크 +
탱커 가드 딜타임 + 카운터/무력화/전멸기(LOS 은신) 기믹 + Pillar 오브젝트.

기존 src/boss (BossRaidEnv) 와 독립. 학습 호환 불필요.
"""
from .config import (
    RaidConfig, PartyRole, PatternID, PhaseID, RaidActionID, BossActionID,
    ROLE_STATS, ROLE_SKILLS, SKILL_KEYS, OBS_LAYOUT,
)
from .shapes import Shape, RelShape, bake_shapes, sample_danger_sensor
from .patterns import PatternStep, PatternDef, PATTERN_REGISTRY, GIMMICK_COOLDOWNS
from .boss import Boss, ActivePattern
from .env import RaidEnv, PartyUnit, Pillar
from .fsm_npc import FSMNpcPolicy
from .rewards import RewardComputer

__all__ = [
    "RaidConfig", "PartyRole", "PatternID", "PhaseID", "RaidActionID", "BossActionID",
    "ROLE_STATS", "ROLE_SKILLS", "SKILL_KEYS", "OBS_LAYOUT",
    "Shape", "RelShape", "bake_shapes", "sample_danger_sensor",
    "PatternStep", "PatternDef", "PATTERN_REGISTRY", "GIMMICK_COOLDOWNS",
    "Boss", "ActivePattern",
    "RaidEnv", "PartyUnit", "Pillar",
    "FSMNpcPolicy", "RewardComputer",
]
