using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BossRaid.EditorTools
{
    /// <summary>
    /// BossStage 씬에 레이드 게임 플로우 시스템을 배선하는 에디터 스크립트.
    ///
    /// - "RaidSystem" GameObject 를 멱등 생성하고 RaidSession + GameFlowUI 를 부착한다.
    /// - 다른 에이전트들이 병행 작성 중인 컴포넌트(RaidHUD, RaidPlayerController,
    ///   CinematicDirector, RaidVFXManager)도 "있으면 부착"한다.
    ///     → 컴파일 순서/병행 작성으로 아직 타입이 없을 수 있으므로 하드 타입 참조 대신
    ///       이름 기반 조회로 부착한다(타입이 없어도 Assembly-CSharp 컴파일이 깨지지 않음).
    /// - 기존 DealerPlayerController / BossHUD 컴포넌트가 씬에 있으면 비활성화(제거 아님).
    ///
    /// 실행: 메뉴 Tools/Boss Arena/Setup Raid Flow  또는 배치 SetupBatch().
    /// </summary>
    public static class RaidFlowSetup
    {
        private const string ScenePath = "Assets/Scenes/Boss/BossStage.unity";
        private const string SystemName = "RaidSystem";

        // 병행 작성 중이라 아직 없을 수 있는 컴포넌트들 (이름 기반 부착)
        private static readonly string[] OptionalComponents =
        {
            "RaidHUD",
            "RaidPlayerController",
            "CinematicDirector",
            "RaidVFXManager",
        };

        [MenuItem("Tools/Boss Arena/Setup Raid Flow")]
        public static void SetupMenu()
        {
            var scene = SceneManager.GetActiveScene();
            bool isBossStage = scene.IsValid() && scene.path == ScenePath;
            if (!isBossStage)
            {
                if (!EditorUtility.DisplayDialog("Setup Raid Flow",
                        $"BossStage 씬을 열고 배선합니다.\n({ScenePath})", "진행", "취소"))
                    return;
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            SetupInScene();
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("[RaidFlowSetup] 배선 완료. 저장하려면 Ctrl+S 또는 SetupBatch 사용.");
        }

        /// <summary>배치용: BossStage 씬을 열고 배선한 뒤 저장한다.</summary>
        public static void SetupBatch()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            SetupInScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[RaidFlowSetup] SetupBatch 완료 및 저장: {ScenePath}");
        }

        // ───────────────────── 배선 본체 ─────────────────────

        private static void SetupInScene()
        {
            // 1) RaidSystem GameObject 멱등 확보
            var system = GameObject.Find(SystemName);
            if (system == null)
            {
                system = new GameObject(SystemName);
                Undo.RegisterCreatedObjectUndo(system, "Create RaidSystem");
                Debug.Log($"[RaidFlowSetup] '{SystemName}' 생성");
            }
            else
            {
                Debug.Log($"[RaidFlowSetup] 기존 '{SystemName}' 재사용");
            }

            // 2) 소유 컴포넌트 직접 부착 (본 스크립트와 함께 컴파일되므로 반드시 존재)
            EnsureComponent(system, typeof(RaidSession));
            EnsureComponent(system, typeof(GameFlowUI));

            // 3) 병행 컴포넌트 이름 기반 부착 (있으면)
            foreach (var name in OptionalComponents)
            {
                var t = FindComponentType(name);
                if (t != null)
                    EnsureComponent(system, t);
                else
                    Debug.Log($"[RaidFlowSetup] (건너뜀) 아직 타입 없음: {name}");
            }

            // 4) 기존 통신/HUD 컴포넌트 비활성화 (제거 아님)
            DisableExisting("DealerPlayerController");
            DisableExisting("BossHUD");

            EditorUtility.SetDirty(system);
        }

        private static void EnsureComponent(GameObject go, Type type)
        {
            if (type == null) return;
            if (go.GetComponent(type) != null)
            {
                Debug.Log($"[RaidFlowSetup] 이미 부착됨: {type.Name}");
                return;
            }
            Undo.AddComponent(go, type);
            Debug.Log($"[RaidFlowSetup] 부착: {type.Name}");
        }

        // 씬 내 해당 타입의 모든 Behaviour 를 enabled=false 로.
        private static void DisableExisting(string typeName)
        {
            var t = FindComponentType(typeName);
            if (t == null) return;
            var comps = UnityEngine.Object.FindObjectsByType(t, FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var c in comps)
            {
                if (c is Behaviour b && b.enabled)
                {
                    Undo.RecordObject(b, "Disable " + typeName);
                    b.enabled = false;
                    EditorUtility.SetDirty(b);
                    Debug.Log($"[RaidFlowSetup] 비활성화: {typeName} on '{b.gameObject.name}'");
                }
            }
        }

        // 로드된 어셈블리에서 MonoBehaviour 파생 타입을 이름으로 조회 (BossRaid 네임스페이스 우선).
        private static Type FindComponentType(string simpleName)
        {
            Type fallback = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                foreach (var t in types)
                {
                    if (t == null || t.Name != simpleName) continue;
                    if (!typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    if (t.Namespace == "BossRaid") return t; // 우선순위
                    fallback ??= t;
                }
            }
            return fallback;
        }
    }
}
