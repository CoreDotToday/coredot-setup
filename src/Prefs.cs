// 항목마다 업데이트를 어떻게 할지 (자동 · 알림만 · 안 함) — PC 마다 하나 (ProgramData\CoreDot\prefs\prefs.json)
//
// 관리센터 자신은 늘 자동 업데이트 (여기에 없다). 설치 안 된 항목은 무엇이든 알리지 않는다.
// prefs 폴더는 사용자도 쓸 수 있다 — 여기 적힌 것은 '깔린 것을 언제 올릴지' 뿐이라, 서비스는 이 파일을 믿고 무엇을 실행하지 않는다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CoreDot
{
    /// <summary>자동 업데이트 시도 기록 (사용자 LocalAppData — 자동 확인이 사용자 권한으로 돈다). 같은 목표 판이 3번 연달아
    /// 실패하면(설치 실패 · 깔았는데 판이 그대로) 하루 쉰다 — 3시간마다 프로그램을 닫고 다시 까는 일을 끝없이 되풀이하지 않게.</summary>
    public static class AutoTries
    {
        const int MaxFails = 3;
        static readonly TimeSpan Rest = TimeSpan.FromHours(24);
        static readonly string File_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"CoreDot\autoupdate.json");

        public static Dictionary<string, object> Load()
        {
            try
            {
                if (File.Exists(File_)) return Core.ParseJson(File.ReadAllText(File_, Encoding.UTF8));
            }
            catch (Exception e) { Core.Log("자동 업데이트 기록 읽기 실패: " + e.Message); }
            return new Dictionary<string, object>();
        }

        public static void Save(Dictionary<string, object> d)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File_));
                File.WriteAllText(File_, Core.ToJson(d), Encoding.UTF8);
            }
            catch (Exception e) { Core.Log("자동 업데이트 기록 저장 실패: " + e.Message); }
        }

        /// <summary>쉬는 중이면 true (같은 목표로 MaxFails 번 실패하고 Rest 가 안 지남).</summary>
        public static bool Resting(Dictionary<string, object> d, string id, string to)
        {
            var e = d.ContainsKey(id) ? d[id] as Dictionary<string, object> : null;
            if (e == null || Core.Str(e, "to") != to) return false;
            long fails = Core.Num(e, "fails");
            DateTime last;
            if (fails < MaxFails || !DateTime.TryParse(Core.Str(e, "last"), null, System.Globalization.DateTimeStyles.RoundtripKind, out last)) return false;
            return DateTime.UtcNow - last.ToUniversalTime() < Rest;
        }

        public static void Record(Dictionary<string, object> d, string id, string to, bool ok)
        {
            if (ok) { d.Remove(id); return; }
            var e = d.ContainsKey(id) ? d[id] as Dictionary<string, object> : null;
            long fails = e != null && Core.Str(e, "to") == to ? Core.Num(e, "fails") : 0;
            var n = new Dictionary<string, object>();
            n["to"] = to ?? "";
            n["fails"] = fails + 1;
            n["last"] = DateTime.UtcNow.ToString("o");
            d[id] = n;
            Core.Log("자동 업데이트 실패 기록: " + id + " → " + to + " (" + (fails + 1) + "번째" + (fails + 1 >= MaxFails ? " — 하루 쉼" : "") + ")");
        }
    }

    /// <summary>알림 창은 하루에 한 번만 — 마지막으로 띄운 때를 사용자 LocalAppData 에 적는다.
    /// ([나중에] 를 눌러도 다음 날 다시. 자동 업데이트는 창이 없으니 상관없이 3시간마다)</summary>
    public static class NotifyOnce
    {
        static readonly TimeSpan Every = TimeSpan.FromHours(24);
        static readonly string File_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"CoreDot\notify.txt");

        public static bool Due()
        {
            try
            {
                DateTime last;
                if (!File.Exists(File_) || !DateTime.TryParse(File.ReadAllText(File_).Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out last)) return true;
                return DateTime.UtcNow - last.ToUniversalTime() >= Every;
            }
            catch { return true; }
        }

        public static void Mark()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File_));
                File.WriteAllText(File_, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception e) { Core.Log("알림 시각 저장 실패: " + e.Message); }
        }
    }

    public static class Prefs
    {
        public const string Auto = "auto", Notify = "notify", Off = "off";
        public static readonly string Dir = Path.Combine(Paths.DataDir, "prefs");
        public static readonly string File_ = Path.Combine(Dir, "prefs.json");
        static readonly object gate = new object();

        public static readonly string[] Modes = { Auto, Notify, Off };

        public static string Label(string mode)
        {
            return mode == Notify ? "알림만" : mode == Off ? "업데이트 안 함" : "자동 업데이트";
        }

        static string Norm(string m)
        {
            return m == Notify || m == Off ? m : Auto;
        }

        /// <summary>id → 방식. 파일이 없거나 못 읽으면 빈 목록 (전부 자동).</summary>
        public static Dictionary<string, string> Load()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(File_)) return d;
                var o = Core.ParseJson(File.ReadAllText(File_, Encoding.UTF8));
                var u = o.ContainsKey("update") ? o["update"] as Dictionary<string, object> : null;
                if (u != null) foreach (var kv in u) d[kv.Key] = Norm(kv.Value == null ? null : kv.Value.ToString());
            }
            catch (Exception e) { Core.Log("설정 읽기 실패: " + e.Message); }
            return d;
        }

        public static string Get(string id)
        {
            string m;
            return Load().TryGetValue(id, out m) ? m : Auto;
        }

        public static bool Set(string id, string mode)
        {
            return SetMany(new Dictionary<string, string> { { id, mode } });
        }

        public static bool SetMany(Dictionary<string, string> changes)
        {
            lock (gate)
            {
                try
                {
                    var d = Load();
                    foreach (var kv in changes) d[kv.Key] = Norm(kv.Value);
                    var u = new Dictionary<string, object>();
                    foreach (var kv in d) u[kv.Key] = kv.Value;
                    var o = new Dictionary<string, object>();
                    o["update"] = u;
                    Directory.CreateDirectory(Dir);
                    string tmp = File_ + "." + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tmp";
                    File.WriteAllText(tmp, Core.ToJson(o), Encoding.UTF8);
                    if (File.Exists(File_)) File.Replace(tmp, File_, null);
                    else File.Move(tmp, File_);
                    foreach (var kv in changes) Core.Log("업데이트 방식: " + kv.Key + " = " + Label(Norm(kv.Value)));
                    return true;
                }
                catch (Exception e) { Core.Log("설정 저장 실패: " + e.Message); return false; }
            }
        }
    }
}
