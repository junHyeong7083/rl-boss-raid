using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 보스레이드 에셋 이전 후 BossStage 씬 정리 유틸(에디터 전용).
/// 레거시 "Input" 오브젝트 제거 + Missing Script 컴포넌트 일괄 정리.
/// 외부 자동화가 RaidMigrationCleanup.Run() 으로 호출.
/// </summary>
public static class RaidMigrationCleanup
{
    // 대상 씬 경로(단일 진입점)
    private const string ScenePath = "Assets/Scenes/Boss/BossStage.unity";

    [MenuItem("Tools/Raid/Migration Cleanup")]
    public static void Run()
    {
        // 안전장치: 플레이 모드 중에는 씬 편집 금지
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogError("[RaidMigrationCleanup] 플레이 모드 중에는 실행할 수 없습니다.");
            return;
        }

        // 안전장치: 씬 파일 존재 확인
        if (!System.IO.File.Exists(ScenePath))
        {
            Debug.LogError($"[RaidMigrationCleanup] 씬 파일을 찾을 수 없습니다: {ScenePath}");
            return;
        }

        // 1. 씬 열기(단일 모드)
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        int deletedInputObjects = 0;
        int removedMissingScripts = 0;

        // 2. 루트에서 이름이 "Input" 인 레거시 오브젝트를 통째로 삭제
        //    (레거시 DealerPlayerController 전용 오브젝트였음)
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == "Input")
            {
                Object.DestroyImmediate(root);
                deletedInputObjects++;
            }
        }

        // 3. 씬 전체 GameObject 순회하며 Missing Script 컴포넌트 제거
        //    (비활성 오브젝트/자식 포함, Input 삭제 후 남은 루트 기준으로 재수집)
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            // 자기 자신 + 모든 자식(비활성 포함) 수집
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            foreach (Transform t in all)
            {
                GameObject go = t.gameObject;
                int before = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (before > 0)
                {
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                    removedMissingScripts += before;
                }
            }
        }

        // 4. 변경사항 저장
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        // 5. 요약 로그
        Debug.Log(
            $"[RaidMigrationCleanup] 완료 — 삭제한 'Input' 오브젝트: {deletedInputObjects}개, " +
            $"제거한 Missing Script: {removedMissingScripts}개. 씬 저장됨: {ScenePath}");
    }
}
