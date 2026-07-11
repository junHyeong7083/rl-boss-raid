using UnityEngine;
using UnityEngine.UI;

namespace BossRaid
{
    /// <summary>
    /// 좌측 4인 파티 프레임(세로 스택).
    /// 각 행: 역할 아이콘(색 사각 + 약칭 D/T/H/S), 이름(Dealer=Player),
    /// HP 바(초록→빨강), 사망 시 회색+해골, buff_atk/buff_shield/buff_guard 색점, marked 표식.
    /// RaidHUD 가 Build/Refresh 를 호출한다(자체 이벤트 구독 없음).
    /// </summary>
    public class PartyFrameUI : MonoBehaviour
    {
        private const int Slots = 4;   // 0=Dealer 1=Tank 2=Healer 3=Support

        // 역할별 아이콘 색 / 약칭 / 이름
        private static readonly Color[] RoleColors =
        {
            new Color(0.90f, 0.28f, 0.28f, 1f), // Dealer - 붉은
            new Color(0.30f, 0.52f, 0.95f, 1f), // Tank   - 파랑
            new Color(0.35f, 0.85f, 0.45f, 1f), // Healer - 초록
            new Color(0.85f, 0.55f, 0.90f, 1f), // Support- 보라핑크
        };
        private static readonly string[] RoleAbbr = { "D", "T", "H", "S" };
        private static readonly string[] RoleName = { "Player", "Tank", "Healer", "Support" };

        // 버프 색점
        private static readonly Color BuffAtk    = new Color(1.00f, 0.45f, 0.15f, 1f); // 공버프 주황
        private static readonly Color BuffShield = new Color(0.30f, 0.80f, 1.00f, 1f); // 실드 하늘
        private static readonly Color BuffGuard  = new Color(0.90f, 0.78f, 0.35f, 1f); // 가드 금색
        private static readonly Color MarkColor  = new Color(0.75f, 0.25f, 0.95f, 1f); // 표식 보라

        private static readonly Color HpGreen = new Color(0.35f, 0.85f, 0.35f, 1f);
        private static readonly Color HpRed   = new Color(0.88f, 0.20f, 0.18f, 1f);
        private static readonly Color DeadGray = new Color(0.40f, 0.40f, 0.42f, 1f);

        private Font _font;

        // 슬롯(역할 인덱스)별 위젯
        private class Row
        {
            public GameObject root;
            public Image roleIcon;
            public Text roleAbbr;
            public Text nameText;
            public RectTransform hpFill;
            public Image hpFillImg;
            public Text hpText;
            public Text skull;      // 사망 해골
            public Image buffAtk;
            public Image buffShield;
            public Image buffGuard;
            public Image mark;
            public bool assigned;   // 이번 스냅샷에서 채워졌는지
        }
        private readonly Row[] _rows = new Row[Slots];

        public void Build(Canvas canvas, Font font)
        {
            _font = font;

            // 좌측 컨테이너 (앵커: left-center)
            var panel = RaidUIFactory.NewImage("PartyFrame", canvas.transform, new Color(0, 0, 0, 0));
            RaidUIFactory.Place(panel.rectTransform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(16f, 40f), new Vector2(250f, Slots * 72f));

            for (int i = 0; i < Slots; i++)
                _rows[i] = BuildRow(panel.transform, i);
        }

        private Row BuildRow(Transform parent, int slot)
        {
            var r = new Row();

            var rowPanel = RaidUIFactory.NewImage($"Row{slot}", parent, RaidUIFactory.PanelDark);
            RaidUIFactory.Place(rowPanel.rectTransform,
                new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, -slot * 72f), new Vector2(250f, 64f));
            r.root = rowPanel.gameObject;

            // 역할 아이콘(색 사각)
            r.roleIcon = RaidUIFactory.NewImage("RoleIcon", rowPanel.transform, RoleColors[slot]);
            RaidUIFactory.Place(r.roleIcon.rectTransform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(8f, 0f), new Vector2(44f, 44f));

            r.roleAbbr = RaidUIFactory.NewText("Abbr", r.roleIcon.transform, _font, 24, Color.white, TextAnchor.MiddleCenter);
            RaidUIFactory.Stretch(r.roleAbbr.rectTransform, 0, 0, 0, 0);
            r.roleAbbr.fontStyle = FontStyle.Bold;
            r.roleAbbr.text = RoleAbbr[slot];

            // 이름
            r.nameText = RaidUIFactory.NewText("Name", rowPanel.transform, _font, 16, Color.white, TextAnchor.LowerLeft);
            RaidUIFactory.Place(r.nameText.rectTransform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0f),
                new Vector2(60f, 2f), new Vector2(150f, 22f));
            r.nameText.text = RoleName[slot];

            // HP 바
            r.hpFillImg = RaidUIFactory.NewBar("Hp", rowPanel.transform,
                new Vector2(0f, 0.5f), new Vector2(0f, 1f),
                new Vector2(60f, 0f), new Vector2(178f, 16f),
                RaidUIFactory.BorderWhite, RaidUIFactory.TrackDark, HpGreen, out r.hpFill);

            r.hpText = RaidUIFactory.NewText("HpText", r.hpFillImg.transform.parent, _font, 11, Color.white, TextAnchor.MiddleCenter);
            RaidUIFactory.Stretch(r.hpText.rectTransform, 0, 0, 0, 0);

            // 사망 해골(기본 숨김)
            r.skull = RaidUIFactory.NewText("Skull", rowPanel.transform, _font, 26, new Color(0.9f, 0.9f, 0.9f), TextAnchor.MiddleCenter);
            RaidUIFactory.Place(r.skull.rectTransform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(8f, 0f), new Vector2(44f, 44f));
            r.skull.text = "☠"; // ☠
            r.skull.gameObject.SetActive(false);

            // 버프 색점 (하단 우측 나란히)
            r.buffAtk    = MakeDot("BuffAtk", rowPanel.transform, BuffAtk,    new Vector2(-8f, 4f));
            r.buffShield = MakeDot("BuffShield", rowPanel.transform, BuffShield, new Vector2(-24f, 4f));
            r.buffGuard  = MakeDot("BuffGuard", rowPanel.transform, BuffGuard,  new Vector2(-40f, 4f));

            // 표식 아이콘 (우상단 다이아)
            r.mark = RaidUIFactory.NewImage("Mark", rowPanel.transform, MarkColor);
            RaidUIFactory.Place(r.mark.rectTransform,
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-6f, -6f), new Vector2(14f, 14f));
            r.mark.rectTransform.localRotation = Quaternion.Euler(0, 0, 45f);
            r.mark.gameObject.SetActive(false);

            return r;
        }

        private Image MakeDot(string name, Transform parent, Color c, Vector2 pos)
        {
            var img = RaidUIFactory.NewImage(name, parent, c);
            RaidUIFactory.Place(img.rectTransform,
                new Vector2(1f, 0f), new Vector2(1f, 0f), pos, new Vector2(12f, 12f));
            img.gameObject.SetActive(false);
            return img;
        }

        // ─────────────── 갱신 ───────────────

        public void Refresh(BossSnapshot snap)
        {
            for (int i = 0; i < Slots; i++)
                if (_rows[i] != null) _rows[i].assigned = false;

            if (snap.units != null)
            {
                foreach (var u in snap.units)
                {
                    if (u == null) continue;
                    if (u.role < 0 || u.role >= Slots) continue;
                    ApplyUnit(_rows[u.role], u);
                }
            }
        }

        private void ApplyUnit(Row r, UnitData u)
        {
            if (r == null) return;
            r.assigned = true;

            bool alive = u.alive && u.hp > 0;
            float ratio = Mathf.Clamp01((float)u.hp / Mathf.Max(1, u.max_hp));

            // HP 바 (초록→빨강)
            RaidUIFactory.SetFill(r.hpFill, alive ? ratio : 0f);
            if (r.hpFillImg != null) r.hpFillImg.color = alive ? Color.Lerp(HpRed, HpGreen, ratio) : DeadGray;
            if (r.hpText != null) r.hpText.text = alive ? $"{u.hp}/{u.max_hp}" : "";

            // 사망 표현: 회색 + 해골, 아이콘/이름 회색
            r.skull.gameObject.SetActive(!alive);
            r.roleIcon.gameObject.SetActive(alive);
            r.roleIcon.color = alive ? RoleColors[u.role] : DeadGray;
            r.nameText.color = alive ? Color.white : DeadGray;

            // 버프 색점
            r.buffAtk.gameObject.SetActive(alive && u.buff_atk > 0);
            r.buffShield.gameObject.SetActive(alive && u.buff_shield > 0);
            r.buffGuard.gameObject.SetActive(alive && u.buff_guard > 0);

            // 표식
            r.mark.gameObject.SetActive(alive && u.marked);
        }
    }
}
