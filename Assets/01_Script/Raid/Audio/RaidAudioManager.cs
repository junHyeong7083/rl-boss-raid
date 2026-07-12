using System.Collections;
using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 스냅샷 이벤트 → 합성 SFX 재생 배선기. 완전 self-contained MonoBehaviour.
    /// AudioSource 풀(2D) + viewer.OnSnapshotApplied 구독으로 동작한다.
    /// 씬 배선은 RaidHUD.Awake 의 CreateChild&lt;RaidAudioManager&gt;("Audio") 한 줄뿐.
    ///
    /// 소비 계약(읽기 전용): BossGameViewer.OnSnapshotApplied / LatestSnapshot,
    ///   BossSnapshot.events(EventData.type/uid/crit/skill_id/kind), boss.active_pattern/hp,
    ///   snap.victory/wipe/done. 재생부만 교체하면 추후 SFX 팩으로 대체 가능.
    /// </summary>
    [DisallowMultipleComponent]
    public class RaidAudioManager : MonoBehaviour
    {
        [Header("Refs (비워두면 자동 탐색)")]
        [SerializeField] private BossGameViewer viewer;

        [Header("Volume")]
        [Range(0f, 1f)] [SerializeField] private float masterVolume = 0.6f;
        [Range(0f, 1f)] [SerializeField] private float actionVolume = 1.0f;   // 타격/스킬/기믹
        [Range(0f, 1f)] [SerializeField] private float uiVolume = 0.8f;       // 클릭/차임/팡파레
        [Range(0f, 1f)] [SerializeField] private float ambientVolume = 0.9f;  // 경고/사이렌/포효

        private enum Cat { Action, Ui, Ambient }

        [Header("Pool")]
        [SerializeField] private int voiceCount = 8;

        private AudioSource[] _voices;
        private int _voiceCursor;
        private bool _subscribed;

        // ─── 에피소드/패턴 상태(에지 감지) ───
        private int _prevPattern = -999;
        private bool _episodeEnded;

        // ─────────────────────────── 라이프사이클 ───────────────────────────

        private void Awake()
        {
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            BuildVoicePool();
        }

        private void OnEnable() => TrySubscribe();

        private void Start()
        {
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            TrySubscribe();
        }

        private void OnDisable()
        {
            if (_subscribed && viewer != null)
            {
                viewer.OnSnapshotApplied -= OnSnapshot;
                _subscribed = false;
            }
        }

        private void TrySubscribe()
        {
            if (_subscribed || viewer == null) return;
            viewer.OnSnapshotApplied += OnSnapshot;
            _subscribed = true;
        }

        private void BuildVoicePool()
        {
            int n = Mathf.Max(1, voiceCount);
            _voices = new AudioSource[n];
            for (int i = 0; i < n; i++)
            {
                var go = new GameObject("Voice" + i);
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 0f;      // 2D
                src.loop = false;
                src.dopplerLevel = 0f;
                _voices[i] = src;
            }
        }

        // ─────────────────────────── 스냅샷 처리 ───────────────────────────

        private void OnSnapshot(BossSnapshot snap)
        {
            if (snap == null) return;

            DetectPatternChange(snap);
            DispatchEvents(snap);
            DetectEpisodeEnd(snap);
        }

        /// <summary>보스 active_pattern 이 새 패턴으로 바뀌면 경고음(전멸기는 cinematic_start 가 담당하므로 제외).</summary>
        private void DetectPatternChange(BossSnapshot snap)
        {
            if (snap.boss == null) return;
            int p = snap.boss.active_pattern;
            if (p != _prevPattern)
            {
                if (p >= 0 && p != (int)RaidPatternId.SealWipe && _prevPattern != -999)
                    Play(SfxKind.Warning, Cat.Ambient, 0.75f);
                _prevPattern = p;
            }
        }

        /// <summary>events 순회 → SFX 매핑 재생.</summary>
        private void DispatchEvents(BossSnapshot snap)
        {
            if (snap.events == null) return;

            int playerHits = 0;       // 같은 스냅샷 내 플레이어 평타 히트 재생 수(스팸 방지 최대 2)
            bool guardPlayed = false; // guard_success 는 브로드캐스트 → 스냅샷당 1회

            foreach (var ev in snap.events)
            {
                if (ev == null || string.IsNullOrEmpty(ev.type)) continue;

                switch (ev.type)
                {
                    case "damage":
                        // 플레이어(uid==0) 것만. 스냅샷당 최대 2회. 피치 ±8% 랜덤.
                        if (ev.uid != 0) break;
                        if (playerHits >= 2) break;
                        playerHits++;
                        Play(SfxKind.Hit, Cat.Action, 1f, RandPitch(0.08f));
                        if (ev.crit) Play(SfxKind.HitCrit, Cat.Action, 0.9f, RandPitch(0.06f));
                        break;

                    case "player_skill_cast":
                        FireSkillCast(ev);
                        break;

                    case "dash":
                        Play(SfxKind.Dash, Cat.Action, 0.9f, RandPitch(0.06f));
                        break;

                    case "counter_success":
                        Play(SfxKind.Counter, Cat.Action, 1f);
                        break;

                    case "counter_miss":
                        Play(SfxKind.UiClick, Cat.Ui, 0.6f, 0.7f);   // 낮게
                        break;

                    case "guard_success":
                        if (guardPlayed) break;
                        guardPlayed = true;
                        Play(SfxKind.Guard, Cat.Action, 1f);
                        break;

                    case "taunt":
                        Play(SfxKind.Guard, Cat.Action, 0.7f, 1.25f);  // 가드 톤을 밝게 = 도발 외침 근사
                        break;

                    case "heal":
                        Play(SfxKind.HealChime, Cat.Ui, 0.9f);
                        break;

                    case "buff":
                        Play(SfxKind.BuffChime, Cat.Ui, 0.9f);
                        break;

                    case "stagger_success":
                        Play(SfxKind.Counter, Cat.Action, 1f, 0.8f);   // Counter 변형(낮은 피치)
                        Play(SfxKind.BossRoar, Cat.Ambient, 0.6f, 1.1f);
                        break;

                    case "stagger_fail":
                        Play(SfxKind.BossRoar, Cat.Ambient, 0.85f);
                        break;

                    case "death":
                        Play(SfxKind.Guard, Cat.Action, 0.9f, 0.55f);  // 저역 둔탁
                        break;

                    case "phase_clear":
                        Play(SfxKind.Warning, Cat.Ambient, 0.9f, 0.9f);
                        break;

                    case "cinematic_start":
                        Play(SfxKind.SealAlarm, Cat.Ambient, 1f);
                        break;

                    case "seal_success":
                        Play(SfxKind.Victory, Cat.Ui, 0.7f, 1.15f);    // 짧게/밝게
                        break;

                    case "seal_fail":
                        Play(SfxKind.Defeat, Cat.Ui, 0.7f, 1.1f);
                        break;

                    case "rush_pillar_hit":
                        Play(SfxKind.Explosion, Cat.Action, 0.7f, 1.2f);
                        break;
                }
            }
        }

        /// <summary>설치기 스킬 시전음. basic=평타(발사 슉 약하게), skill=혈창 발사, skill2=혈월(임팩트 지연).</summary>
        private void FireSkillCast(EventData ev)
        {
            switch (ev.skill_id)
            {
                case "skill":
                    Play(SfxKind.Throw, Cat.Action, 1f, RandPitch(0.05f));
                    break;
                case "skill2":
                    // 혈월 낙하: 투사체 비행시간 고려해 임팩트를 0.35s 지연 재생.
                    Play(SfxKind.Throw, Cat.Action, 0.5f, 0.7f);       // 발사 슉(약하게)
                    StartCoroutine(DelayedPlay(0.35f, SfxKind.Explosion, Cat.Action, 1f, 1f));
                    break;
                default: // "basic"
                    // 평타의 "타격음(Hit)"은 동반 damage(uid==0) 이벤트가 담당 → 여기선 스윙 슉만(중복 방지).
                    // 빗나가면 damage 이벤트가 없어 슉만 나므로 명중/빗나감이 자연히 구분됨.
                    Play(SfxKind.Throw, Cat.Action, 0.35f, 1.3f);      // 발사 슉(약하게)
                    break;
            }
        }

        /// <summary>승리/패배 감지(에피소드당 1회). snap.victory/wipe 우선, 보스 hp 0 / 전멸 폴백.</summary>
        private void DetectEpisodeEnd(BossSnapshot snap)
        {
            bool bossDead = snap.boss != null && snap.boss.hp <= 0;
            bool allDead = AllUnitsDead(snap);
            bool ended = snap.done || snap.victory || snap.wipe || bossDead || allDead;

            if (ended && !_episodeEnded)
            {
                _episodeEnded = true;
                bool win = snap.victory || (bossDead && !snap.wipe);
                Play(win ? SfxKind.Victory : SfxKind.Defeat, Cat.Ui, 1f);
            }
            else if (!ended)
            {
                _episodeEnded = false;   // 새 에피소드 시작 → 재무장
            }
        }

        private static bool AllUnitsDead(BossSnapshot snap)
        {
            if (snap.units == null || snap.units.Length == 0) return false;
            foreach (var u in snap.units)
                if (u != null && u.alive) return false;
            return true;
        }

        // ─────────────────────────── 재생 ───────────────────────────

        private IEnumerator DelayedPlay(float delay, SfxKind kind, Cat cat, float vol, float pitch)
        {
            yield return new WaitForSecondsRealtime(delay);   // 히트스톱과 무관하게 VFX 투사체 비행(unscaled)과 정렬
            Play(kind, cat, vol, pitch);
        }

        private void Play(SfxKind kind, Cat cat, float volumeScale, float pitch = 1f)
        {
            if (_voices == null || _voices.Length == 0) return;
            var clip = ProceduralSfx.Get(kind);
            if (clip == null) return;

            var src = NextVoice();
            src.pitch = pitch;
            float vol = masterVolume * CategoryVolume(cat) * Mathf.Clamp01(volumeScale);
            src.PlayOneShot(clip, vol);
        }

        private float CategoryVolume(Cat cat)
        {
            switch (cat)
            {
                case Cat.Ui:      return uiVolume;
                case Cat.Ambient: return ambientVolume;
                default:          return actionVolume;
            }
        }

        /// <summary>라운드로빈으로 다음 보이스 선택(가급적 재생 중이지 않은 것 우선).</summary>
        private AudioSource NextVoice()
        {
            int n = _voices.Length;
            for (int k = 0; k < n; k++)
            {
                var s = _voices[(_voiceCursor + k) % n];
                if (s != null && !s.isPlaying)
                {
                    _voiceCursor = (_voiceCursor + k + 1) % n;
                    return s;
                }
            }
            var v = _voices[_voiceCursor];
            _voiceCursor = (_voiceCursor + 1) % n;
            return v;
        }

        private static float RandPitch(float amount)
            => 1f + Random.Range(-amount, amount);
    }
}
