using UnityEngine;

namespace BossRaid
{
    /// <summary>
    /// 저주 연결(CursedChain) 시각화 - 두 유닛 사이 LineRenderer.
    /// 거리가 3칸 초과 시 색이 붉게 변함 (위험 표시).
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class ChainLine : MonoBehaviour
    {
        public Color safeColor = new Color(1f, 0f, 1f, 0.8f);
        public Color dangerColor = new Color(1f, 0.2f, 0.2f, 1f);
        public float dangerDistance = 3f;   // 월드 거리 기준 (cellSize 1 기준)

        private LineRenderer _line;
        private Transform _a, _b;

        public void Setup(Transform a, Transform b)
        {
            _a = a; _b = b;
            if (_line == null) _line = GetComponent<LineRenderer>();
            _line.positionCount = 2;
            _line.startWidth = 0.1f;
            _line.endWidth = 0.1f;
        }

        private void Update()
        {
            if (_a == null || _b == null || _line == null) return;
            _line.SetPosition(0, _a.position + Vector3.up * 0.5f);
            _line.SetPosition(1, _b.position + Vector3.up * 0.5f);
            float d = Vector3.Distance(_a.position, _b.position);
            Color c = d > dangerDistance ? dangerColor : safeColor;
            _line.startColor = c;
            _line.endColor = c;
        }
    }
}
