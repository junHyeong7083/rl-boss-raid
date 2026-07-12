using System.Collections.Generic;
using System.Text;

namespace BossRaid
{
    /// <summary>
    /// Python snapshot(JSON)용 경량 파서.
    /// JsonUtility가 int[][]를 못 읽어서 직접 구현.
    /// 외부 의존성(Newtonsoft 등) 없이 MiniJSON 스타일로 구현.
    /// </summary>
    internal static class BossJsonParser
    {
        public static BossSnapshot Parse(string json)
        {
            int i = 0;
            object root = ParseValue(json, ref i);
            if (!(root is Dictionary<string, object> d)) return null;

            var snap = new BossSnapshot
            {
                step = GetInt(d, "step"),
                done = GetBool(d, "done"),
                victory = GetBool(d, "victory"),
                wipe = GetBool(d, "wipe"),
                boss = ParseBoss(GetDict(d, "boss")),
                units = ParseUnits(GetList(d, "units")),
                telegraphs = ParseTelegraphs(GetList(d, "telegraphs")),
                events = ParseEvents(GetList(d, "events")),
                pillars = ParsePillars(GetList(d, "pillars")),
            };
            return snap;
        }

        // V2: 기둥 배열 파싱 (없으면 빈 배열 → 하위 호환)
        private static PillarData[] ParsePillars(List<object> list)
        {
            if (list == null) return new PillarData[0];
            var arr = new PillarData[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i] as Dictionary<string, object>;
                arr[i] = new PillarData
                {
                    x = GetFloat(d, "x"),
                    y = GetFloat(d, "y"),
                    radius = GetFloat(d, "radius"),
                    alive = GetBoolOrDefault(d, "alive", true),
                    respawn_in = GetInt(d, "respawn_in"),
                };
            }
            return arr;
        }

        private static EventData[] ParseEvents(List<object> list)
        {
            if (list == null) return new EventData[0];
            var arr = new EventData[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i] as Dictionary<string, object>;
                arr[i] = new EventData
                {
                    uid = GetIntOrDefault(d, "uid", -1),
                    type = GetString(d, "type"),
                    amount = GetInt(d, "amount"),
                    target = GetIntOrDefault(d, "target", -1),
                    skill = GetBool(d, "skill"),
                    crit = GetBool(d, "crit"),   // 없으면 false (하위 호환)
                    kind = GetString(d, "kind"),
                    // V2 설치기: "skill" 키가 문자열이면 skill_id 로 (bool 이면 GetString 이 null 반환 → 무해)
                    skill_id = GetString(d, "skill"),
                    tx = GetFloat(d, "tx"),
                    ty = GetFloat(d, "ty"),
                    radius = GetFloat(d, "radius"),
                    hit = GetBool(d, "hit"),
                    // counter_miss/parry_fail 사유. 없으면 null.
                    reason = GetString(d, "reason"),
                    // pillar_explode 위치 (sim 좌표). 없으면 0.
                    x = GetFloat(d, "x"),
                    y = GetFloat(d, "y"),
                };
            }
            return arr;
        }

        private static BossData ParseBoss(Dictionary<string, object> d)
        {
            if (d == null) return null;
            return new BossData
            {
                x = GetFloat(d, "x"),
                y = GetFloat(d, "y"),
                vx = GetFloat(d, "vx"),
                vy = GetFloat(d, "vy"),
                hp = GetInt(d, "hp"),
                max_hp = GetInt(d, "max_hp"),
                phase = GetInt(d, "phase"),
                invuln = GetInt(d, "invuln"),
                grog = GetInt(d, "grog"),
                stagger_active = GetBool(d, "stagger_active"),
                stagger_gauge = GetFloat(d, "stagger_gauge"),
                radius = GetFloat(d, "radius"),
                // ── V2 (없으면 기본값) ──
                facing = GetFloat(d, "facing"),
                stun = GetInt(d, "stun"),
                counter_window = GetInt(d, "counter_window"),
                active_pattern = GetIntOrDefault(d, "active_pattern", -1),
                active_mode = GetString(d, "active_mode") ?? "",
                // ── 돌진 표식 (없으면 비활성) ──
                rush_target = GetIntOrDefault(d, "rush_target", -1),
                rush_left = GetInt(d, "rush_left"),
                // ── 원형 아레나 / 무력화 게이지 (없으면 0) ──
                arena_radius = GetFloat(d, "arena_radius"),
                stagger_max = GetInt(d, "stagger_max"),
            };
        }

        private static UnitData[] ParseUnits(List<object> list)
        {
            if (list == null) return new UnitData[0];
            var arr = new UnitData[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i] as Dictionary<string, object>;
                arr[i] = new UnitData
                {
                    uid = GetInt(d, "uid"),
                    role = GetInt(d, "role"),
                    x = GetFloat(d, "x"),
                    y = GetFloat(d, "y"),
                    vx = GetFloat(d, "vx"),
                    vy = GetFloat(d, "vy"),
                    hp = GetInt(d, "hp"),
                    max_hp = GetInt(d, "max_hp"),
                    alive = GetBool(d, "alive"),
                    marked = GetBool(d, "marked"),
                    chained_with = GetIntOrDefault(d, "chained_with", -1),
                    buff_atk = GetInt(d, "buff_atk"),
                    buff_shield = GetInt(d, "buff_shield"),
                    radius = GetFloat(d, "radius"),
                    // ── V2 (없으면 기본값) ──
                    buff_guard = GetInt(d, "buff_guard"),
                    cooldowns = GetIntDict(d, "cooldowns"),
                };
            }
            return arr;
        }

        private static TelegraphData[] ParseTelegraphs(List<object> list)
        {
            if (list == null) return new TelegraphData[0];
            var arr = new TelegraphData[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var d = list[i] as Dictionary<string, object>;
                var shapesRaw = GetList(d, "shapes");
                ShapeData[] shapes = new ShapeData[shapesRaw?.Count ?? 0];
                for (int s = 0; s < shapes.Length; s++)
                {
                    shapes[s] = ParseShape(shapesRaw[s] as Dictionary<string, object>);
                }
                var targetsRaw = GetList(d, "target_uids");
                int[] targets = new int[targetsRaw?.Count ?? 0];
                for (int t = 0; t < targets.Length; t++)
                    targets[t] = (int)(long)targetsRaw[t];

                arr[i] = new TelegraphData
                {
                    pattern = GetInt(d, "pattern"),
                    turns_remaining = GetInt(d, "turns_remaining"),
                    total_wind_up = GetInt(d, "total_wind_up"),
                    shapes = shapes,
                    target_uids = targets,
                    // ── V2 (없으면 기본값) ──
                    step_index = GetInt(d, "step_index"),
                    num_steps = GetInt(d, "num_steps"),
                    anim = GetString(d, "anim") ?? "",
                };
            }
            return arr;
        }

        private static ShapeData ParseShape(Dictionary<string, object> d)
        {
            if (d == null) return new ShapeData();
            return new ShapeData
            {
                kind = GetString(d, "kind"),
                cx = GetFloat(d, "cx"),
                cy = GetFloat(d, "cy"),
                r = GetFloat(d, "r"),
                angle = GetFloat(d, "angle"),
                width = GetFloat(d, "width"),
                ax = GetFloat(d, "ax"),
                ay = GetFloat(d, "ay"),
                bx = GetFloat(d, "bx"),
                by = GetFloat(d, "by"),
                hw = GetFloat(d, "hw"),
                safe_mask = GetFloat(d, "safe_mask"),
                // ── V2 donut ──
                r_in = GetFloat(d, "r_in"),
                r_out = GetFloat(d, "r_out"),
            };
        }

        private static string GetString(Dictionary<string, object> d, string k)
            => d != null && d.TryGetValue(k, out var v) && v is string s ? s : null;

        // ── Helpers ──
        private static int GetInt(Dictionary<string, object> d, string k)
            => d != null && d.TryGetValue(k, out var v) && v != null ? (int)(long)v : 0;

        private static int GetIntOrDefault(Dictionary<string, object> d, string k, int def)
            => d != null && d.TryGetValue(k, out var v) && v != null ? (int)(long)v : def;

        private static float GetFloat(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null) return 0f;
            if (v is double dd) return (float)dd;
            if (v is long ll) return ll;
            return 0f;
        }

        private static bool GetBool(Dictionary<string, object> d, string k)
            => d != null && d.TryGetValue(k, out var v) && v is bool b && b;

        private static bool GetBoolOrDefault(Dictionary<string, object> d, string k, bool def)
            => d != null && d.TryGetValue(k, out var v) && v is bool b ? b : def;

        // V2: {"skill":0,"counter":3} 같은 JSON object → Dictionary<string,int>
        private static Dictionary<string, int> GetIntDict(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.TryGetValue(k, out var v) || !(v is Dictionary<string, object> raw))
                return null;
            var result = new Dictionary<string, int>(raw.Count);
            foreach (var kv in raw)
            {
                if (kv.Value is long l) result[kv.Key] = (int)l;
                else if (kv.Value is double db) result[kv.Key] = (int)db;
            }
            return result;
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string k)
            => d != null && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;

        private static List<object> GetList(Dictionary<string, object> d, string k)
            => d != null && d.TryGetValue(k, out var v) ? v as List<object> : null;

        // ── Minimal JSON tokenizer ──
        private static object ParseValue(string s, ref int i)
        {
            SkipWhite(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' || c == 'f') return ParseBool(s, ref i);
            if (c == 'n') { i += 4; return null; }
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>();
            i++; // {
            SkipWhite(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                SkipWhite(s, ref i);
                var key = ParseString(s, ref i);
                SkipWhite(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                var val = ParseValue(s, ref i);
                d[key] = val;
                SkipWhite(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
            }
            return d;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var l = new List<object>();
            i++; // [
            SkipWhite(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (i < s.Length)
            {
                l.Add(ParseValue(s, ref i));
                SkipWhite(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
            }
            return l;
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening "
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char nx = s[i + 1];
                    if (nx == 'n') sb.Append('\n');
                    else if (nx == 't') sb.Append('\t');
                    else if (nx == 'r') sb.Append('\r');
                    else sb.Append(nx);
                    i += 2;
                    continue;
                }
                sb.Append(s[i]);
                i++;
            }
            i++; // closing "
            return sb.ToString();
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            bool isFloat = false;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+'))
            {
                if (s[i] == '.' || s[i] == 'e' || s[i] == 'E') isFloat = true;
                i++;
            }
            string num = s.Substring(start, i - start);
            if (isFloat) return double.Parse(num, System.Globalization.CultureInfo.InvariantCulture);
            return long.Parse(num, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool ParseBool(string s, ref int i)
        {
            if (s[i] == 't') { i += 4; return true; }
            i += 5; return false;
        }

        private static void SkipWhite(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r'))
                i++;
        }
    }
}
