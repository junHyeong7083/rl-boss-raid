using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;      // SceneManager.GetActiveScene
using UnityEngine.Rendering;            // Volume, VolumeProfile
using UnityEngine.Rendering.Universal;  // Bloom, Tonemapping, ColorAdjustments, Vignette, URP camera data
using Object = UnityEngine.Object;

namespace BossRaid.EditorTools
{
    /// <summary>
    /// 보스 아레나 프리뷰 스크린샷 캡처 에디터 툴.
    /// - 임시 프리뷰 더미(보스/유닛/텔레그래프)를 "Preview_Generated" 루트에 스폰
    /// - LostArkCamera 파라미터로 카메라 배치(플레이 없이 직접 계산)
    /// - 에디트 모드용 임시 글로벌 Volume 로 포스트FX 구성
    /// - RenderTexture 1920×1080 캡처 후 PNG 저장, 더미/볼륨 정리(씬 미저장)
    /// </summary>
    public static class BossViewCapture
    {
        private const string ScenePath   = "Assets/Scenes/Boss/BossStage.unity";
        private const string PreviewRoot = "Preview_Generated";
        private const int Width  = 1920;
        private const int Height = 1080;

        // 프리뷰 배치 좌표(연속 좌표계 = 월드; cellSize=1, origin=0 기준)
        private static readonly Vector3 BossPos = new Vector3(10f, 0f, 10f);
        private static readonly Vector3[] UnitPos =
        {
            new Vector3(7.5f, 0f, 6.5f),   // 0 Dealer (카메라 포커스 대상)
            new Vector3(9f,   0f, 8.5f),   // 1 Tank
            new Vector3(6.5f, 0f, 5f),     // 2 Healer
            new Vector3(8f,   0f, 5.5f),   // 3 Support
        };

        // ─────────────── 진입점 ───────────────

        [MenuItem("Tools/Boss Arena/Capture Preview")]
        private static void CaptureMenu()
        {
            EnsureSceneOpen();
            RunCapture();
        }

        /// <summary>배치 모드용: 씬 열기 → 캡처. 실패 시 Exit(1).</summary>
        public static void CaptureBatch()
        {
            try
            {
                EnsureSceneOpen();
                RunCapture();
                Debug.Log("[BossViewCapture] CaptureBatch 완료.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[BossViewCapture] CaptureBatch 실패: {e}");
                EditorApplication.Exit(1);
            }
        }

        private static void EnsureSceneOpen()
        {
            var active = SceneManager.GetActiveScene();
            if (active.path != ScenePath)
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        // ─────────────── 캡처 본체 ───────────────

        private static void RunCapture()
        {
            var viewer = Object.FindFirstObjectByType<BossGameViewer>();
            var camGo = FindInScene("Main Camera");
            var cam = camGo != null ? camGo.GetComponent<Camera>() : Camera.main;
            if (cam == null) throw new Exception("Main Camera(Camera 컴포넌트)를 찾을 수 없음.");

            // 카메라 원상복구용 상태 백업
            var prevPos = cam.transform.position;
            var prevRot = cam.transform.rotation;
            float prevFov = cam.fieldOfView;
            var prevRT = cam.targetTexture;
            bool prevHDR = cam.allowHDR;

            GameObject previewRoot = null;
            GameObject volumeGo = null;
            VolumeProfile profile = null;

            try
            {
                // 1) 프리뷰 더미 스폰
                previewRoot = new GameObject(PreviewRoot);
                SpawnPreview(viewer, previewRoot.transform);

                // 2) 임시 포스트FX 볼륨
                volumeGo = new GameObject("Temp_PreviewVolume");
                volumeGo.transform.SetParent(previewRoot.transform, false);
                profile = BuildTempVolume(volumeGo);

                // 3) 카메라 후처리 활성화
                cam.allowHDR = true;
                var camData = cam.GetUniversalAdditionalCameraData();
                if (camData != null) camData.renderPostProcessing = true;

                // 4) 앵글별 캡처
                Vector3 focus = UnitPos[0] + Vector3.up * 1f;   // 딜러 + (0,1,0)
                string outPath = ResolveOutputPath();
                CaptureAngle(cam, focus, 20f, 24f, outPath);

                string angles = Environment.GetEnvironmentVariable("BOSS_CAPTURE_ANGLES");
                if (!string.IsNullOrEmpty(angles) &&
                    angles.IndexOf("wide", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    CaptureAngle(cam, focus, 28f, 32f, WithSuffix(outPath, "_wide"));
                }
            }
            finally
            {
                // 5) 정리: 더미/볼륨 삭제, 카메라 원복 (씬 저장 안 함)
                cam.targetTexture = prevRT;
                cam.transform.SetPositionAndRotation(prevPos, prevRot);
                cam.fieldOfView = prevFov;
                cam.allowHDR = prevHDR;

                if (previewRoot != null) Object.DestroyImmediate(previewRoot);
                if (profile != null) Object.DestroyImmediate(profile);
            }
        }

        /// <summary>LostArkCamera 파라미터(pitch 55, back*distance, fov)를 직접 계산해 렌더/저장.</summary>
        private static void CaptureAngle(Camera cam, Vector3 focus, float distance, float fov, string outPath)
        {
            Quaternion rot = Quaternion.Euler(55f, 0f, 0f);
            Vector3 back = rot * Vector3.back;
            cam.transform.SetPositionAndRotation(focus + back * distance, rot);
            cam.fieldOfView = fov;

            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.DefaultHDR)
            {
                antiAliasing = 1
            };
            var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            var prevActive = RenderTexture.active;
            try
            {
                // URP(SRP)에서 cam.Render()는 미지원 → StandardRequest로 전체 스택(포스트FX 포함) 렌더
                var request = new RenderPipeline.StandardRequest();
                if (RenderPipeline.SupportsRenderRequest(cam, request))
                {
                    request.destination = rt;
                    RenderPipeline.SubmitRenderRequest(cam, request);
                }
                else
                {
                    cam.targetTexture = rt;   // 비SRP fallback
                    cam.Render();
                }

                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                tex.Apply();

                var dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(outPath, tex.EncodeToPNG());
                Debug.Log($"[BossViewCapture] 저장: {outPath}");
            }
            finally
            {
                RenderTexture.active = prevActive;
                cam.targetTexture = null;
                Object.DestroyImmediate(tex);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }

        // ─────────────── 프리뷰 더미 ───────────────

        private static void SpawnPreview(BossGameViewer viewer, Transform root)
        {
            // 보스
            GameObject bossPrefab = viewer != null ? viewer.bossPrefab : null;
            SpawnUnit(bossPrefab, BossPos, root, "Preview_Boss", 1f);

            // 유닛 4역할
            for (int i = 0; i < UnitPos.Length; i++)
            {
                GameObject prefab = null;
                if (viewer != null && viewer.unitPrefabsByRole != null && i < viewer.unitPrefabsByRole.Length)
                    prefab = viewer.unitPrefabsByRole[i];
                SpawnUnit(prefab, UnitPos[i], root, $"Preview_Unit_{i}", 0.6f);
            }

            // 텔레그래프 프리뷰 2개 (tileMarkerPrefab 있을 때만)
            var markerPrefab = viewer != null ? viewer.tileMarkerPrefab : null;
            if (markerPrefab != null)
            {
                // (a) 보스 위치 반경 3 원형, Fill 0.65
                var circle = InstantiateChild(markerPrefab, root);
                circle.name = "Preview_TG_Circle";
                circle.transform.position = BossPos + Vector3.up * 0.02f;
                circle.transform.localScale = new Vector3(6f, 6f, 1f);   // d = r*2 = 6
                circle.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                ConfigureTelegraph(circle, shapeType: 0, fill: 0.65f, fanHalfRad: 0.785f,
                    baseCol: new Color(1.0f, 0.35f, 0.05f, 0.9f));   // 용암 주황(Eruption 톤)

                // (b) 보스→딜러 방향 부채꼴, Fill 0.4
                var fan = InstantiateChild(markerPrefab, root);
                fan.name = "Preview_TG_Fan";
                fan.transform.position = BossPos + Vector3.up * 0.02f;
                fan.transform.localScale = new Vector3(12f, 12f, 1f);  // d = r*2 = 12 (r=6)
                var dir = UnitPos[0] - BossPos; dir.y = 0f;
                // Y축 회전 θ는 +X를 (cosθ, 0, -sinθ)로 보내므로 목표각 부호 반전 필요 (미반전 시 Z축 미러)
                float yaw = -Mathf.Atan2(dir.z, dir.x) * Mathf.Rad2Deg;
                fan.transform.rotation = Quaternion.Euler(0f, yaw, 0f) * Quaternion.Euler(90f, 0f, 0f);
                ConfigureTelegraph(fan, shapeType: 1, fill: 0.4f, fanHalfRad: 0.6f,
                    baseCol: new Color(1.0f, 0.55f, 0.1f, 0.9f));      // 주황(Slash 톤)
            }
        }

        private static void SpawnUnit(GameObject prefab, Vector3 pos, Transform root, string name, float scale)
        {
            GameObject go;
            if (prefab != null)
            {
                go = InstantiateChild(prefab, root);
                go.transform.localScale *= scale;
            }
            else
            {
                // fallback: Capsule
                go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.transform.SetParent(root, true);
                go.transform.localScale = Vector3.one * scale;
                pos += Vector3.up * scale;   // 캡슐 발이 바닥에 닿도록 살짝 올림
            }
            go.name = name;
            go.transform.position = pos;
        }

        private static GameObject InstantiateChild(GameObject prefab, Transform root)
        {
            var go = PrefabUtility.InstantiatePrefab(prefab, root.gameObject.scene) as GameObject;
            if (go == null) go = Object.Instantiate(prefab);
            go.transform.SetParent(root, true);
            return go;
        }

        /// <summary>텔레그래프 머티리얼 프로퍼티 직접 세팅(에디트 모드 안전 — TileMarker.Update 미의존).</summary>
        private static void ConfigureTelegraph(GameObject go, int shapeType, float fill,
            float fanHalfRad, Color baseCol)
        {
            var rend = go.GetComponentInChildren<Renderer>();
            if (rend == null || rend.sharedMaterial == null) return;
            // 에디트 모드에서 rend.material 접근은 머티리얼 누수 에러 로그를 냄
            // → sharedMaterial 복제본을 만들어 교체 (프리팹 원본 안전, 루트째 DestroyImmediate로 정리)
            var m = new Material(rend.sharedMaterial);
            rend.sharedMaterial = m;

            if (m.HasProperty("_ShapeType")) m.SetInt("_ShapeType", shapeType);
            if (m.HasProperty("_FanWidthRad")) m.SetFloat("_FanWidthRad", fanHalfRad);
            if (m.HasProperty("_Fill")) m.SetFloat("_Fill", fill);
            if (m.HasProperty("_Progress")) m.SetFloat("_Progress", fill);
            if (m.HasProperty("_Pulse")) m.SetFloat("_Pulse", 0f);
            if (m.HasProperty("_SafeMask")) m.SetFloat("_SafeMask", 0f);
            if (m.HasProperty("_Color")) m.SetColor("_Color", baseCol);
            if (m.HasProperty("_OutlineColor"))
            {
                Color outCol = baseCol * 2.5f; outCol.a = 1f;
                m.SetColor("_OutlineColor", outCol);
            }
        }

        // ─────────────── 임시 볼륨 ───────────────

        private static VolumeProfile BuildTempVolume(GameObject go)
        {
            var vol = go.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 20f;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "PreviewPostFX_Temp";
            vol.sharedProfile = profile;

            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.value = 1.0f;
            bloom.intensity.value = 0.8f;
            bloom.scatter.value = 0.7f;

            var tone = profile.Add<Tonemapping>(true);
            tone.mode.value = TonemappingMode.ACES;

            var ca = profile.Add<ColorAdjustments>(true);
            ca.postExposure.value = 0.15f;
            ca.contrast.value = 15f;
            ca.saturation.value = 10f;

            var vig = profile.Add<Vignette>(true);
            vig.intensity.value = 0.25f;

            return profile;
        }

        // ─────────────── 출력 경로 ───────────────

        /// <summary>BOSS_CAPTURE_OUT 있으면 그 경로, 없으면 프로젝트 루트/Captures/preview.png.</summary>
        private static string ResolveOutputPath()
        {
            string env = Environment.GetEnvironmentVariable("BOSS_CAPTURE_OUT");
            if (!string.IsNullOrEmpty(env)) return env;
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, "Captures", "preview.png");
        }

        private static string WithSuffix(string path, string suffix)
        {
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string file = name + suffix + ext;
            return string.IsNullOrEmpty(dir) ? file : Path.Combine(dir, file);
        }

        // ─────────────── 유틸 ───────────────

        private static GameObject FindInScene(string name)
        {
            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == name) return root;
                var t = FindDeep(root.transform, name);
                if (t != null) return t.gameObject;
            }
            return null;
        }

        private static Transform FindDeep(Transform t, string name)
        {
            foreach (Transform c in t)
            {
                if (c.name == name) return c;
                var r = FindDeep(c, name);
                if (r != null) return r;
            }
            return null;
        }
    }
}
