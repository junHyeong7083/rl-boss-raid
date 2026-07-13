using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 아군/적 색 팔레트 단일 소스 — "구분이 안 된다" 피드백 대응.
    ///
    /// 원칙:
    ///   적(보스) 위험 = 진홍/붉은 계열  (대비의 기준 — 절대 아군 이펙트에 쓰지 않는다)
    ///   아군(플레이어/NPC) 스킬 = 청록/하늘/금 계열
    ///
    /// 두 종류의 값을 제공한다:
    ///   *Aim*  : 조준 외곽 발광 등 HDR(블룸 대응, 채널 &gt;1 허용) 용도.
    ///   그 외   : VFX 베이스 색(0..1, RaidVFXManager 가 HdrColor 로 증폭).
    /// 하드코딩 산재를 막기 위해 RaidPlayerController / RaidVFXManager / SkillAimIndicator 가
    /// 이 상수를 참조한다.
    /// </summary>
    public static class RaidPalette
    {
        // ── 아군 조준 외곽 발광 (HDR) ──
        public static readonly Color AllyAimTeal  = new Color(0.2f, 1.6f, 1.4f, 1f);   // Q 혈창 투척: 청록
        public static readonly Color AllyAimSky   = new Color(0.3f, 0.9f, 1.8f, 1f);   // W 혈월 낙하: 하늘
        public static readonly Color AllyAimGold  = new Color(2.1f, 1.5f, 0.45f, 1f);  // R 혈월 처형: 금색(보스 red 와 구분)
        public static readonly Color AllyAimBasic = new Color(1.2f, 1.5f, 1.7f, 1f);   // 평타/폴백: 연한 백청

        // ── 아군 VFX 베이스 (0..1) ──
        public static readonly Color AllyTeal      = new Color(0.20f, 1.00f, 0.92f);   // Q 청록
        public static readonly Color AllySky       = new Color(0.35f, 0.78f, 1.00f);   // W 하늘
        public static readonly Color AllyGold      = new Color(1.00f, 0.82f, 0.25f);   // R 금색
        public static readonly Color AllyWhiteBlue = new Color(0.85f, 0.92f, 1.00f);   // 평타 백청

        // ── 적(보스) — 진홍/붉은 (대비 기준) ──
        public static readonly Color EnemyCrimson = new Color(0.85f, 0.06f, 0.12f);
        public static readonly Color EnemyRed     = new Color(1.00f, 0.16f, 0.16f);
    }
}
