using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BossRaid.EditorTools
{
    /// <summary>
    /// 신규 캐릭터 5종(보스 Demon + 파티 Rogue/Warrior/Cleric/Wizard)의
    /// 임포트 세팅 · URP 머티리얼 리맵 · AnimatorController · 프리팹 · 씬 배선을
    /// 배치 한 번에 멱등하게 자동 구성한다.
    ///
    /// 실행: 메뉴 Tools/Boss Arena/Setup Characters  또는 배치 SetupBatch().
    /// 재실행 안전(멱등): 머티리얼/컨트롤러/프리팹은 고정 경로에 갱신, 씬 참조도 재배선.
    ///
    /// 파이프라인:
    ///   1) AssetDatabase.Refresh()  (신규 FBX/PNG 첫 임포트 → .meta 생성)
    ///   2) FBX 임포트 세팅(rig=Generic) + URP Lit 머티리얼 외부 리맵 + 클립 loop
    ///   3) AnimatorController 5개 생성 (Assets/05_Animator/Raid/)
    ///   4) 프리팹 5개 생성 (Assets/02_Prefab/Raid/), 스케일 정규화 + 컴포넌트 배선
    ///   5) BossStage 씬의 BossGameViewer.bossPrefab / unitPrefabsByRole 재배선
    /// </summary>
    public static class CharacterSetup
    {
        // ─────────────── 고정 경로 ───────────────
        private const string ScenePath   = "Assets/Scenes/Boss/BossStage.unity";
        private const string CharRoot     = "Assets/09_Characters";
        private const string MatFolder    = "Assets/04_Material/Characters";
        private const string CtrlFolder   = "Assets/05_Animator/Raid";
        private const string PrefabFolder = "Assets/02_Prefab/Raid";

        // 보스 이펙트 프리팹 (있으면 보스 프리팹에 복제)
        private const string FxInvuln  = "Assets/02_Prefab/BossEffect/InvulnEffect.prefab";
        private const string FxGroggy  = "Assets/02_Prefab/BossEffect/GroggyEffect.prefab";
        private const string FxStagger = "Assets/02_Prefab/BossEffect/StaggerEffect .prefab"; // 파일명에 트레일링 스페이스

        // ─────────────── 캐릭터 정의 ───────────────
        private class CharDef
        {
            public string prefabName;          // Boss_Demon / Unit_Dealer_Rogue ...
            public string fbxPath;
            public bool   isBoss;
            public int    role;                // 유닛만: 0=Dealer,1=Tank,2=Healer,3=Support (보스=-1)
            public float  targetHeight;        // 바운즈 기준 정규화 높이
            public (string mat, string tex)[] materials;  // FBX 내부 머티리얼명 → baseMap PNG
        }

        private static CharDef[] BuildDefs()
        {
            const string P = CharRoot + "/Party/";
            return new[]
            {
                new CharDef {
                    prefabName = "Boss_Demon", isBoss = true, role = -1, targetHeight = 2.8f,   // 3.5는 장판 대비 과대 (캡처 검수)
                    fbxPath = CharRoot + "/Boss/Demon.fbx",
                    materials = new[] { ("Atlas", CharRoot + "/Boss/Atlas_Monsters.png") },
                },
                new CharDef {
                    prefabName = "Unit_Dealer_Rogue", role = 0, targetHeight = 1.7f,
                    fbxPath = P + "Rogue.fbx",
                    materials = new[] { ("Rogue_Texture", P + "Rogue_Texture.png"),
                                        ("Rogue_Dagger_Texture", P + "Rogue_Dagger_Texture.png") },
                },
                new CharDef {
                    prefabName = "Unit_Tank_Warrior", role = 1, targetHeight = 1.7f,
                    fbxPath = P + "Warrior.fbx",
                    materials = new[] { ("Warrior_Texture", P + "Warrior_Texture.png"),
                                        ("Warrior_Sword_Texture", P + "Warrior_Sword_Texture.png") },
                },
                new CharDef {
                    prefabName = "Unit_Healer_Cleric", role = 2, targetHeight = 1.7f,
                    fbxPath = P + "Cleric.fbx",
                    materials = new[] { ("Cleric_Texture", P + "Cleric_Texture.png"),
                                        ("Cleric_Staff_Texture", P + "Cleric_Staff_Texture.png") },
                },
                new CharDef {
                    prefabName = "Unit_Support_Wizard", role = 3, targetHeight = 1.7f,
                    fbxPath = P + "Wizard.fbx",
                    materials = new[] { ("Wizard_Texture", P + "Wizard_Texture.png"),
                                        ("Wizard_Staff_Texture", P + "Wizard_Staff_Texture.png") },
                },
            };
        }

        // 파티 유닛 역할 순서: 0=Dealer,1=Tank,2=Healer,3=Support
        private static readonly string[] RoleToPrefab =
        {
            "Unit_Dealer_Rogue", "Unit_Tank_Warrior", "Unit_Healer_Cleric", "Unit_Support_Wizard",
        };

        // 보스 V2 anim 키 11종 → 클립 후보(우선순위). 최종 폴백은 Punch.
        // slash/smash/shock/brand/spin→Punch, rush→Run(speed 1.5), throw/lift→Jump, roar/blood_moon/counter_glow→Wave
        private struct BossKey { public string key; public string[] clip; public float speed; }
        private static readonly BossKey[] BossKeys =
        {
            new BossKey { key = "slash",        clip = new[]{ "Punch" },              speed = 1f },
            new BossKey { key = "smash",        clip = new[]{ "Punch" },              speed = 1f },
            new BossKey { key = "shock",        clip = new[]{ "Punch" },              speed = 1f },
            new BossKey { key = "brand",        clip = new[]{ "Punch" },              speed = 1f },
            new BossKey { key = "spin",         clip = new[]{ "Punch" },              speed = 1f },
            new BossKey { key = "rush",         clip = new[]{ "Run" },                speed = 1.5f },
            new BossKey { key = "throw",        clip = new[]{ "Jump", "Jump_Idle" },  speed = 1f },
            new BossKey { key = "lift",         clip = new[]{ "Jump", "Jump_Idle" },  speed = 1f },
            new BossKey { key = "roar",         clip = new[]{ "Wave" },               speed = 1f },
            new BossKey { key = "blood_moon",   clip = new[]{ "Wave" },               speed = 1f },
            new BossKey { key = "counter_glow", clip = new[]{ "Wave" },               speed = 1f },
        };

        // ─────────────── 진입점 ───────────────

        [MenuItem("Tools/Boss Arena/Setup Characters")]
        public static void SetupMenu()
        {
            Run(saveScene: false);
            Debug.Log("[CharacterSetup] Setup Characters 완료 (씬 저장은 수동 Ctrl+S 또는 SetupBatch).");
        }

        /// <summary>배치용: BossStage 씬 열기 → 전체 수행 → 씬 저장.</summary>
        public static void SetupBatch()
        {
            Run(saveScene: true);
        }

        private static void Run(bool saveScene)
        {
            // 5) 신규 FBX/PNG 첫 임포트 (.meta 생성)
            AssetDatabase.Refresh();

            EnsureFolder(MatFolder);
            EnsureFolder(CtrlFolder);
            EnsureFolder(PrefabFolder);

            var defs = BuildDefs();

            // BossStage 씬을 연다 (프리팹 임시 오브젝트 생성/씬 배선용)
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // 1) 임포트 세팅 + 머티리얼 리맵 + 클립 loop
            foreach (var d in defs) ConfigureImporter(d);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // 2) AnimatorController 5개
            var ctrls = new Dictionary<string, AnimatorController>();
            foreach (var d in defs) ctrls[d.prefabName] = BuildController(d);

            // 3) 프리팹 5개
            var prefabs = new Dictionary<string, GameObject>();
            foreach (var d in defs) prefabs[d.prefabName] = BuildPrefab(d, ctrls[d.prefabName]);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // 4) 씬 배선
            WireScene(prefabs);

            EditorSceneManager.MarkSceneDirty(scene);
            if (saveScene)
            {
                EditorSceneManager.SaveScene(scene);
                Debug.Log($"[CharacterSetup] SetupBatch 완료 및 씬 저장: {ScenePath}");
            }
        }

        // ─────────────── 1. 임포트 세팅 + 머티리얼 ───────────────

        private static void ConfigureImporter(CharDef d)
        {
            var importer = AssetImporter.GetAtPath(d.fbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError($"[CharacterSetup] ModelImporter 없음: {d.fbxPath}");
                return;
            }

            // rig = Generic (materialImportMode 는 유지). 아바타는 이 모델에서 생성.
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;

            // 클립 loop: Idle/Walk/Run 는 공통 루프. 보스는 Groggy 연출용 Duck/HitReact 도 루프.
            var loopSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Idle", "Walk", "Run" };
            if (d.isBoss) { loopSuffixes.Add("Duck"); loopSuffixes.Add("HitReact"); }

            var clips = importer.defaultClipAnimations; // FBX take 기반 기본 클립 목록
            foreach (var c in clips)
                if (loopSuffixes.Contains(SegName(c.name)))
                    c.loopTime = true;
            importer.clipAnimations = clips;

            // URP Lit 머티리얼 생성 후 외부 리맵
            foreach (var (matName, texPath) in d.materials)
            {
                var mat = CreateOrUpdateMaterial(matName, texPath);
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), matName), mat);
            }

            importer.SaveAndReimport();

            // 임포트된 실제 머티리얼 슬롯명이 기대와 다르면 경고(리맵 누락 진단용)
            var authored = AssetDatabase.LoadAllAssetsAtPath(d.fbxPath)
                .OfType<Material>().Select(m => m.name).Distinct().ToArray();
            foreach (var (matName, _) in d.materials)
                if (authored.Length > 0 && !authored.Contains(matName))
                    Debug.LogWarning($"[CharacterSetup] {d.prefabName}: FBX 머티리얼명 '{matName}' 미검출. 실제=[{string.Join(", ", authored)}]");
        }

        private static Material CreateOrUpdateMaterial(string matName, string texPath)
        {
            string path = $"{MatFolder}/{matName}.mat";
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[CharacterSetup] URP/Lit 셰이더를 찾을 수 없음 (URP 미설치?)");
                shader = Shader.Find("Standard");
            }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.shader != shader)
            {
                mat.shader = shader;
            }

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex != null)
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                mat.mainTexture = tex;
            }
            else Debug.LogWarning($"[CharacterSetup] 텍스처 없음: {texPath}");

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ─────────────── 2. AnimatorController ───────────────

        private static AnimatorController BuildController(CharDef d)
        {
            string path = $"{CtrlFolder}/{d.prefabName}.controller";

            // 멱등: 기존 컨트롤러가 있으면 GUID 유지 위해 내용만 초기화 후 재구성
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (ctrl == null) ctrl = AnimatorController.CreateAnimatorControllerAtPath(path);
            else ClearController(ctrl);

            var clips = LoadClips(d.fbxPath);
            var sm = ctrl.layers[0].stateMachine;

            // 공통: Idle(default) / Run / Death
            var idle = sm.AddState("Idle", new Vector3(300, 0, 0));
            idle.motion = FindClip(clips, "Idle");
            sm.defaultState = idle;

            var run = sm.AddState("Run", new Vector3(300, 120, 0));
            run.motion = FindClip(clips, "Run", "Walk");

            var death = sm.AddState("Death", new Vector3(300, 240, 0));
            death.motion = FindClip(clips, "Death");

            ctrl.AddParameter("IsMoving", AnimatorControllerParameterType.Bool);
            ctrl.AddParameter("Dead", AnimatorControllerParameterType.Bool);

            // Idle <-> Run (bool IsMoving, duration 0.15, hasExitTime false)
            AddCond(Trans(idle, run, false, 0.15f), AnimatorConditionMode.If, "IsMoving");
            AddCond(Trans(run, idle, false, 0.15f), AnimatorConditionMode.IfNot, "IsMoving");

            // Death: Any State 진입, 자기 재진입 방지
            var toDeath = AnyTrans(sm, death, 0.1f);
            AddCond(toDeath, AnimatorConditionMode.If, "Dead");
            toDeath.canTransitionToSelf = false;

            if (d.isBoss) BuildBossStates(ctrl, sm, clips, idle, run);
            else          BuildPartyStates(ctrl, sm, clips, idle, run);

            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
            return ctrl;
        }

        private static void BuildPartyStates(AnimatorController ctrl, AnimatorStateMachine sm,
                                             List<AnimationClip> clips, AnimatorState idle, AnimatorState run)
        {
            // UnitView.cs 가 실제로 쓰는 파라미터 이름(우선) : IsMoving/Dead(이미 추가) + 아래 트리거들
            ctrl.AddParameter("TrigAttack", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("TrigHeal",   AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("TrigTaunt",  AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("TrigBuff",   AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("TrigHit",    AnimatorControllerParameterType.Trigger);

            // Hit(RecieveHit): Any State → 자동 Idle 복귀
            var hit = sm.AddState("Hit", new Vector3(600, 240, 0));
            hit.motion = FindClip(clips, "RecieveHit", "RecieveHit_2", "HitReact");
            var toHit = AnyTrans(sm, hit, 0.05f);
            AddCond(toHit, AnimatorConditionMode.If, "TrigHit");
            toHit.canTransitionToSelf = false;
            Trans(hit, idle, true, 0.1f);

            // 공격/스킬: Idle·Run 에서 트리거 진입 + Exit Time 으로 Idle 복귀
            // TrigAttack = 무기 공격, TrigHeal/TrigBuff = Spell 계열, TrigTaunt = 보조 공격
            AddActionState(sm, idle, run, "Attack", "TrigAttack", new Vector3(0, 0, 0),
                FindClip(clips, "Sword_Attack", "Dagger_Attack", "Staff_Attack", "Attacking_Idle", "Punch"));
            AddActionState(sm, idle, run, "Heal", "TrigHeal", new Vector3(0, 120, 0),
                FindClip(clips, "Spell1", "Spell", "Dagger_Attack2", "Sword_AttackFast", "Punch"));
            AddActionState(sm, idle, run, "Buff", "TrigBuff", new Vector3(0, 240, 0),
                FindClip(clips, "Spell2", "Spell1", "Spell", "Punch"));
            AddActionState(sm, idle, run, "Taunt", "TrigTaunt", new Vector3(0, 360, 0),
                FindClip(clips, "Sword_AttackFast", "Idle_Attacking", "Dagger_Attack2", "Punch"));
        }

        private static void BuildBossStates(AnimatorController ctrl, AnimatorStateMachine sm,
                                            List<AnimationClip> clips, AnimatorState idle, AnimatorState run)
        {
            // BossController.cs : Phase(int)/Groggy(bool)/Dead(bool)/IsMoving(bool) + anim 키 11종 트리거
            ctrl.AddParameter("Phase",  AnimatorControllerParameterType.Int);
            ctrl.AddParameter("Groggy", AnimatorControllerParameterType.Bool);

            // Groggy: Any State 진입(Duck/HitReact 루프), Groggy=false 시 Idle 복귀
            var groggy = sm.AddState("Groggy", new Vector3(600, 120, 0));
            groggy.motion = FindClip(clips, "Duck", "HitReact");
            var toGrog = AnyTrans(sm, groggy, 0.15f);
            AddCond(toGrog, AnimatorConditionMode.If, "Groggy");
            toGrog.canTransitionToSelf = false;
            AddCond(Trans(groggy, idle, false, 0.15f), AnimatorConditionMode.IfNot, "Groggy");

            // anim 키 11종: 각각 고유 트리거 + 상태. Idle·Run 에서 진입, Exit Time 으로 Idle 복귀.
            float y = 0f;
            foreach (var bk in BossKeys)
            {
                ctrl.AddParameter(bk.key, AnimatorControllerParameterType.Trigger);
                var clip = FindClip(clips, bk.clip) ?? FindClip(clips, "Punch") ?? idle.motion as AnimationClip;
                var st = AddActionState(sm, idle, run, "Atk_" + bk.key, bk.key, new Vector3(0, y, 0), clip);
                if (st != null) st.speed = bk.speed;
                y += 70f;
            }
        }

        /// <summary>Idle·Run 두 곳에서 트리거로 진입하고 Exit Time 으로 Idle 로 돌아오는 액션 상태를 만든다.</summary>
        private static AnimatorState AddActionState(AnimatorStateMachine sm, AnimatorState idle, AnimatorState run,
                                                    string name, string trigger, Vector3 pos, AnimationClip clip)
        {
            var st = sm.AddState(name, pos);
            st.motion = clip;
            AddCond(Trans(idle, st, false, 0.1f), AnimatorConditionMode.If, trigger);
            AddCond(Trans(run, st, false, 0.1f), AnimatorConditionMode.If, trigger);
            Trans(st, idle, true, 0.1f); // Exit Time → Idle
            return st;
        }

        // ── AnimatorController 헬퍼 ──

        private static AnimatorStateTransition Trans(AnimatorState from, AnimatorState to, bool exitTime, float dur)
        {
            var t = from.AddTransition(to);
            t.hasExitTime = exitTime;
            t.exitTime = exitTime ? 0.85f : 0f;
            t.hasFixedDuration = true;
            t.duration = dur;
            return t;
        }

        private static AnimatorStateTransition AnyTrans(AnimatorStateMachine sm, AnimatorState to, float dur)
        {
            var t = sm.AddAnyStateTransition(to);
            t.hasExitTime = false;
            t.hasFixedDuration = true;
            t.duration = dur;
            return t;
        }

        private static void AddCond(AnimatorStateTransition t, AnimatorConditionMode mode, string param)
            => t.AddCondition(mode, 0f, param);

        private static void ClearController(AnimatorController c)
        {
            var sm = c.layers[0].stateMachine;
            foreach (var t in sm.anyStateTransitions.ToArray()) sm.RemoveAnyStateTransition(t);
            foreach (var t in sm.entryTransitions.ToArray()) sm.RemoveEntryTransition(t);
            foreach (var s in sm.states.ToArray()) sm.RemoveState(s.state);
            foreach (var sub in sm.stateMachines.ToArray()) sm.RemoveStateMachine(sub.stateMachine);
            var ps = c.parameters;
            for (int i = ps.Length - 1; i >= 0; i--) c.RemoveParameter(i);
        }

        // ─────────────── 3. 프리팹 ───────────────

        private static GameObject BuildPrefab(CharDef d, AnimatorController ctrl)
        {
            string path = $"{PrefabFolder}/{d.prefabName}.prefab";

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(d.fbxPath);
            if (model == null)
            {
                Debug.LogError($"[CharacterSetup] FBX 로드 실패: {d.fbxPath}");
                return null;
            }

            // 루트 GO + 모델 인스턴스(자식)
            var root = new GameObject(d.prefabName);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            inst.transform.SetParent(root.transform, false);
            inst.transform.localPosition = Vector3.zero;

            // Animator (모델 인스턴스에 존재) : 컨트롤러 연결 + rootMotion off
            var anim = inst.GetComponent<Animator>();
            if (anim == null) anim = inst.AddComponent<Animator>();
            var avatar = AssetDatabase.LoadAllAssetsAtPath(d.fbxPath).OfType<Avatar>().FirstOrDefault();
            if (avatar != null) anim.avatar = avatar;
            anim.runtimeAnimatorController = ctrl;
            anim.applyRootMotion = false;

            // 스케일 정규화 (바운즈 기준 : 파티 1.7 / 보스 3.5). 측정은 스케일 1 상태에서.
            float h = MeasureHeight(inst);
            if (h > 0.0001f) root.transform.localScale = Vector3.one * (d.targetHeight / h);

            var renderers = inst.GetComponentsInChildren<Renderer>()
                .Where(r => !(r is ParticleSystemRenderer) && !(r is LineRenderer) && !(r is TrailRenderer))
                .ToArray();

            if (d.isBoss)
            {
                var bc = root.AddComponent<BossController>();
                bc.animator = anim;
                bc.bodyRenderers = renderers;
                // 보스 전용 이펙트 자식 복제 (있으면) + 필드 배선
                float torso = h * 0.5f;
                bc.invulnEffect  = AttachEffect(root, FxInvuln,  "InvulnEffect",  new Vector3(0, torso, 0.4f));
                bc.groggyEffect  = AttachEffect(root, FxGroggy,  "GroggyEffect",  new Vector3(0, h * 0.9f, 0.2f));
                bc.staggerEffect = AttachEffect(root, FxStagger, "StaggerEffect", new Vector3(0, h * 0.9f, 0.2f));
            }
            else
            {
                var uv = root.AddComponent<UnitView>();
                uv.animator = anim;
                // HP바/이펙트 자식은 기존 유닛 프리팹에 없어 생략 (task: 없으면 생략)
            }

            var saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            Debug.Log($"[CharacterSetup] 프리팹 저장: {path}");
            return saved;
        }

        private static GameObject AttachEffect(GameObject root, string fxPath, string name, Vector3 localPos)
        {
            var fx = AssetDatabase.LoadAssetAtPath<GameObject>(fxPath);
            if (fx == null) return null;
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(fx);
            inst.name = name;
            inst.transform.SetParent(root.transform, false);
            inst.transform.localPosition = localPos;
            inst.SetActive(false);
            return inst;
        }

        private static float MeasureHeight(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            bool has = false; Bounds b = new Bounds();
            foreach (var r in rs)
            {
                if (r is ParticleSystemRenderer) continue;
                Bounds bb = r.bounds;
                if (bb.size == Vector3.zero)
                {
                    if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                        bb = new Bounds(r.transform.TransformPoint(smr.sharedMesh.bounds.center),
                                        Vector3.Scale(smr.sharedMesh.bounds.size, r.transform.lossyScale));
                    else if (r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null)
                        bb = new Bounds(r.transform.TransformPoint(mf.sharedMesh.bounds.center),
                                        Vector3.Scale(mf.sharedMesh.bounds.size, r.transform.lossyScale));
                    else continue;
                }
                if (!has) { b = bb; has = true; } else b.Encapsulate(bb);
            }
            return has ? b.size.y : 0f;
        }

        // ─────────────── 4. 씬 배선 ───────────────

        private static void WireScene(Dictionary<string, GameObject> prefabs)
        {
            var viewer = Object.FindFirstObjectByType<BossGameViewer>();
            if (viewer == null)
            {
                Debug.LogError("[CharacterSetup] 씬에서 BossGameViewer 를 찾지 못함 — 배선 생략.");
                return;
            }

            var so = new SerializedObject(viewer);
            so.FindProperty("bossPrefab").objectReferenceValue = prefabs.GetValueOrDefault("Boss_Demon");

            var arr = so.FindProperty("unitPrefabsByRole");
            arr.arraySize = 4;
            for (int i = 0; i < 4; i++)
                arr.GetArrayElementAtIndex(i).objectReferenceValue = prefabs.GetValueOrDefault(RoleToPrefab[i]);

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(viewer);
            Debug.Log("[CharacterSetup] BossGameViewer 배선 완료 (bossPrefab + unitPrefabsByRole[4]).");
        }

        // ─────────────── 공통 유틸 ───────────────

        private static List<AnimationClip> LoadClips(string fbxPath)
            => AssetDatabase.LoadAllAssetsAtPath(fbxPath)
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview", StringComparison.Ordinal))
                .ToList();

        /// <summary>"CharacterArmature|Idle" → "Idle" (마지막 '|' 뒤 세그먼트).</summary>
        private static string SegName(string clipName)
        {
            int i = clipName.LastIndexOf('|');
            return i >= 0 ? clipName.Substring(i + 1) : clipName;
        }

        /// <summary>후보 이름들을 우선순위로 탐색. 세그먼트 정확일치 우선, 없으면 부분일치.</summary>
        private static AnimationClip FindClip(List<AnimationClip> clips, params string[] names)
        {
            foreach (var want in names)
                foreach (var c in clips)
                    if (string.Equals(SegName(c.name), want, StringComparison.OrdinalIgnoreCase))
                        return c;
            foreach (var want in names)
                foreach (var c in clips)
                    if (SegName(c.name).IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                        return c;
            return null;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
