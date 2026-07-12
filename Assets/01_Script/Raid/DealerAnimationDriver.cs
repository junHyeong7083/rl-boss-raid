using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace BossRaid
{
    /// <summary>
    /// 딜러 액션별 원샷 모션 재생기. PlayableGraph(AnimationClipPlayable)로 Animator 컨트롤러
    /// 출력 위에 클립을 한 번 재생하고, 끝나면 그래프를 해제해 컨트롤러(Idle/Run/…)로 복귀한다.
    ///
    /// 트리거: BossGameViewer.OnSnapshotApplied 구독 → snap.events 중 "내 uid" 이벤트만 반응.
    ///   dash                         → Roll
    ///   player_skill_cast skill_id=="basic"  → Dagger_Attack
    ///   player_skill_cast skill_id=="skill"  → Dagger_Attack2   (Q 혈창)
    ///   player_skill_cast skill_id=="skill2" → Punch            (W 혈월)
    ///   player_skill_cast skill_id=="ult"    → Dagger_Attack → Dagger_Attack2 연속(0.9배속, 혈월 처형)
    ///
    /// 클립 소스 전략(순서대로):
    ///   (a) animator.runtimeAnimatorController.animationClips 에서 이름으로 조회.
    ///       Unit_Dealer_Rogue.controller 는 Dagger_Attack / Dagger_Attack2 / Punch 를 스텝에 배선하고
    ///       있어 (a) 로 확보된다. (정적 확인: 컨트롤러 m_Motion fileID ↔ Rogue.fbx.meta internalID 대조.)
    ///   (b) (a) 에 없는 클립(예: Roll — 컨트롤러 미배선)은 [SerializeField] extraClips 배열의
    ///       FBX 서브에셋 참조에서 확보. 프리팹에 직렬화돼 있다.
    ///   두 소스 모두에서 못 찾은 클립은 조용히 스킵(컨트롤러 트리거가 대체 — 컴파일/런타임 안전).
    ///
    /// 부착: Unit_Dealer_Rogue.prefab 루트에 직렬화(extraClips 배선) + RaidPlayerController 가
    ///       딜러 발견 시 GetComponent-or-AddComponent(예측기와 동일 패턴)로 이중 안전망.
    /// </summary>
    [DisallowMultipleComponent]
    public class DealerAnimationDriver : MonoBehaviour
    {
        // ─── FBX 클립 이름(“CharacterArmature|…” 접두는 Rogue.fbx export 규약) ───
        private const string ClipRoll    = "CharacterArmature|Roll";
        private const string ClipDagger1 = "CharacterArmature|Dagger_Attack";
        private const string ClipDagger2 = "CharacterArmature|Dagger_Attack2";
        private const string ClipPunch   = "CharacterArmature|Punch";

        [Header("Refs (비우면 자동 탐색)")]
        [Tooltip("스냅샷 이벤트 구독용 조율자. 비우면 씬에서 자동 탐색.")]
        [SerializeField] private BossGameViewer viewer;
        [Tooltip("원샷 클립을 재생할 Animator. 비우면 자식에서 자동 탐색.")]
        [SerializeField] private Animator animator;

        [Header("Clips — 전략(b) 폴백")]
        [Tooltip("컨트롤러 animationClips 로 확보 못 하는 클립(예: Roll)의 FBX 서브에셋 참조. "
               + "이름(clip.name)으로 매핑되므로 컨트롤러에 이미 있는 클립을 넣어도 무해(중복 안전).")]
        [SerializeField] private AnimationClip[] extraClips;

        [Header("Timing")]
        [Tooltip("궁극기(혈월 처형) 연속타 재생 배속. 0.9 = 살짝 묵직하게.")]
        [SerializeField] private float ultSpeed = 0.9f;

        // ─── 내부 상태 ───
        private UnitView _unitView;                 // 같은 GO 의 UnitView(내 uid 조회)
        private bool _subscribed;
        private readonly Dictionary<string, AnimationClip> _clips = new Dictionary<string, AnimationClip>();
        private bool _clipsResolved;
        private PlayableGraph _graph;
        private bool _graphValid;
        private Coroutine _routine;

        private void Awake()
        {
            if (animator == null) animator = GetComponentInChildren<Animator>();
            _unitView = GetComponent<UnitView>();
        }

        private void OnEnable()  => TrySubscribe();
        private void OnDisable()
        {
            Unsubscribe();
            StopPlayback();
        }
        private void OnDestroy()
        {
            Unsubscribe();
            StopPlayback();
        }

        private void Update()
        {
            // viewer/animator 가 늦게 준비될 수 있어 지연 구독/해상.
            if (!_subscribed) TrySubscribe();
        }

        // ─────────────── 구독 ───────────────

        private void TrySubscribe()
        {
            if (_subscribed) return;
            if (viewer == null) viewer = FindFirstObjectByType<BossGameViewer>();
            if (viewer == null) return;
            viewer.OnSnapshotApplied += OnSnapshotApplied;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed || viewer == null) return;
            viewer.OnSnapshotApplied -= OnSnapshotApplied;
            _subscribed = false;
        }

        // ─────────────── 클립 소스 해상((a)→(b)) ───────────────

        private void ResolveClips()
        {
            if (_clipsResolved) return;
            if (animator == null) animator = GetComponentInChildren<Animator>();
            _clips.Clear();

            // (a) 컨트롤러 animationClips (이름으로). Dagger_Attack / Dagger_Attack2 / Punch 확보.
            if (animator != null && animator.runtimeAnimatorController != null)
            {
                foreach (var c in animator.runtimeAnimatorController.animationClips)
                    if (c != null && !_clips.ContainsKey(c.name)) _clips[c.name] = c;
            }

            // (b) 직렬화 폴백 (컨트롤러에 없는 것만 채움 — Roll 등).
            if (extraClips != null)
                foreach (var c in extraClips)
                    if (c != null && !_clips.ContainsKey(c.name)) _clips[c.name] = c;

            _clipsResolved = true;
        }

        private AnimationClip GetClip(string name)
        {
            ResolveClips();
            return _clips.TryGetValue(name, out var c) ? c : null;
        }

        // ─────────────── 이벤트 → 모션 ───────────────

        private void OnSnapshotApplied(BossSnapshot snap)
        {
            if (snap == null || snap.events == null) return;
            if (animator == null) return;

            int myUid = _unitView != null ? _unitView.uid : 0;

            foreach (var ev in snap.events)
            {
                if (ev == null || ev.uid != myUid || string.IsNullOrEmpty(ev.type)) continue;

                if (ev.type == "dash")
                {
                    PlayOneShot(GetClip(ClipRoll), 1f);
                }
                else if (ev.type == "player_skill_cast")
                {
                    switch (ev.skill_id)
                    {
                        case "basic":  PlayOneShot(GetClip(ClipDagger1), 1f); break;
                        case "skill":  PlayOneShot(GetClip(ClipDagger2), 1f); break;
                        case "skill2": PlayOneShot(GetClip(ClipPunch),   1f); break;
                        case "ult":    PlaySequence(new[] { GetClip(ClipDagger1), GetClip(ClipDagger2) }, ultSpeed); break;
                    }
                }
            }
        }

        // ─────────────── PlayableGraph 원샷/연속 ───────────────

        private void PlayOneShot(AnimationClip clip, float speed)
        {
            if (clip == null) return;   // 두 소스 모두 없음 → 컨트롤러 트리거가 대체(안전 스킵)
            PlaySequence(new[] { clip }, speed);
        }

        private void PlaySequence(AnimationClip[] clips, float speed)
        {
            if (animator == null || clips == null) return;
            // 유효 클립만 필터.
            var valid = new List<AnimationClip>(clips.Length);
            foreach (var c in clips) if (c != null) valid.Add(c);
            if (valid.Count == 0) return;

            StopPlayback();
            _routine = StartCoroutine(PlayRoutine(valid, Mathf.Max(0.01f, speed)));
        }

        private IEnumerator PlayRoutine(List<AnimationClip> clips, float speed)
        {
            _graph = PlayableGraph.Create("DealerAnimDriver");
            _graphValid = true;
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);   // 히트스톱 슬로모에 자연 동기
            var output = AnimationPlayableOutput.Create(_graph, "DealerAnim", animator);

            foreach (var clip in clips)
            {
                var playable = AnimationClipPlayable.Create(_graph, clip);
                playable.SetSpeed(speed);
                output.SetSourcePlayable(playable);
                _graph.Play();

                // 클립 길이/배속만큼 대기. Time.deltaTime(스케일드)로 누적 → GameTime 그래프와 동기.
                float dur = clip.length / speed;
                float t = 0f;
                while (t < dur && _graphValid)
                {
                    t += Time.deltaTime;
                    yield return null;
                }
                if (!_graphValid) yield break;   // 외부에서 중단됨(StopPlayback 이 그래프 해제)
            }

            // 정상 종료: 코루틴 스스로는 StopCoroutine 하지 않고 그래프만 해제 → 컨트롤러(Idle/Run) 복귀.
            _routine = null;
            DestroyGraph();
        }

        /// <summary>진행 중인 재생을 중단(새 캐스트 인터럽트/비활성화). 코루틴 정지 + 그래프 해제.</summary>
        private void StopPlayback()
        {
            if (_routine != null)
            {
                StopCoroutine(_routine);
                _routine = null;
            }
            DestroyGraph();
        }

        /// <summary>PlayableGraph 해제(멱등). 해제 시 Animator 는 컨트롤러 출력으로 복귀.</summary>
        private void DestroyGraph()
        {
            if (!_graphValid) return;
            _graphValid = false;
            if (_graph.IsValid()) _graph.Destroy();
        }
    }
}
