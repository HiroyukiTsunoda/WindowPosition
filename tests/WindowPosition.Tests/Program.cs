using WindowPosition.Core;

var tests = new (string Name, Action Run)[]
{
    ("First appearance applies once and respects later user moves", FirstAppearance),
    ("Disappearance and process changes begin a new window lifetime", WindowLifetimes),
    ("Continuous mode reapplies changed bounds and skips correct bounds", Continuous),
    ("Maximized windows are restored even when stored bounds match", Maximized),
    ("Minimized windows do not consume their first appearance", Minimized),
    ("Failed attempts retry in first-appearance mode", RetryFailure),
    ("Asynchronous requests are confirmed and bounded when refused", AsynchronousApply),
    ("Normalized bounds count as confirmed", NormalizedBounds),
    ("Disabled and paused rules do not consume first appearances", DisabledAndPaused),
    ("Configuration changes and explicit reset reapply first-appearance rules", ResetAndConfiguration),
    ("Title matching uses exact or contains without case sensitivity", TitleMatching),
    ("First enabled matching rule owns overlapping windows", ConflictOrder),
    ("ApplyNow reapplies processed windows", ImmediateApply),
    ("Invalid rules cannot move windows", InvalidRules),
    ("Settings round-trip all values and keep only one cfg", SettingsRoundTrip),
    ("Tray startup state round-trips without enlarging an empty cfg", TrayStartupSettings),
    ("Legacy cfg loads and migrates without losing window rules", LegacySettingsMigration),
    ("Corrupt source survives recovery and subsequent save", CorruptSettings),
    ("Invalid settings are rejected without replacing saved data", InvalidSettings),
    ("Truncated, corrupt, unsupported and oversized configs degrade gracefully", CorruptBinary),
    ("Malformed binary counts, fields and encodings are rejected", MalformedBinary),
    ("Signed coordinates and all flag combinations round-trip", IntegerAndFlagExtremes),
    ("Compact file sizes for zero, one and ten Japanese rules", CompactSizeExamples),
    ("All four edges retain the live size and other coordinate", AlignEdges),
    ("Sequential arrows reach every corner", AlignCorners),
    ("Negative monitor origins align in virtual desktop coordinates", AlignNegativeMonitor),
    ("Partly off-screen windows are clamped on both axes", AlignOffScreen),
    ("Invisible resize borders retain their offset at every edge", AlignInvisibleBorders),
    ("Work area excludes taskbars on every monitor edge", AlignWorkArea),
    ("Oversized windows fail without changing size or coordinates", AlignOversized),
    ("Invalid rectangles, directions and coordinate overflows fail", AlignInvalid),
    ("Diagnostic log distinguishes clean and incomplete sessions", DiagnosticLogTests.Sessions),
    ("Diagnostic log preserves nested exception details and abnormal exit", DiagnosticLogTests.Exceptions),
    ("Diagnostic log rotates and bounds UTF-8 records", DiagnosticLogTests.Rotation),
    ("Diagnostic failures never escape into the application", DiagnosticLogTests.WriteFailures),
    ("Concurrent diagnostic events remain complete records", DiagnosticLogTests.ConcurrentWrites)
};

var failed = 0;
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {error}");
    }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static WindowRule Rule() => new() { Title = "Example", X = -1000, Y = 25, Width = 900, Height = 700 };
static WindowSnapshot Snapshot(nint handle = 1, int processId = 10, string title = "Example") =>
    new(handle, processId, title, new(10, 20, 500, 400), false, false);

static void FirstAppearance()
{
    var fake = new FakeWindows(Snapshot());
    var engine = new TrackingEngine(fake);
    var rule = Rule();
    Equal(1, engine.Tick([rule])[0].MatchedCount);
    engine.Tick([rule]);
    fake.Windows[0] = fake.Windows[0] with { Bounds = new(100, 200, 600, 450) };
    engine.Tick([rule]);
    Equal(1, fake.Calls.Count);
}

static void WindowLifetimes()
{
    var fake = new FakeWindows(Snapshot());
    var engine = new TrackingEngine(fake);
    var rule = Rule();
    engine.Tick([rule]);
    fake.Windows.Clear();
    engine.Tick([rule]);
    fake.Windows.Add(Snapshot());
    engine.Tick([rule]);
    fake.Windows[0] = Snapshot(processId: 11);
    engine.Tick([rule]);
    Equal(3, fake.Calls.Count);
}

static void Continuous()
{
    var fake = new FakeWindows(Snapshot());
    var engine = new TrackingEngine(fake);
    var rule = Rule() with { Mode = FollowMode.Continuous };
    engine.Tick([rule]);
    engine.Tick([rule]);
    Equal(1, fake.Calls.Count);
    fake.Windows[0] = Snapshot();
    engine.Tick([rule]);
    Equal(2, fake.Calls.Count);
}

static void Maximized()
{
    var rule = Rule();
    var fake = new FakeWindows(Snapshot() with { IsMaximized = true, Bounds = rule.Bounds });
    new TrackingEngine(fake).Tick([rule]);
    Equal(1, fake.Calls.Count);
    Equal(false, fake.Windows[0].IsMaximized);
}

static void Minimized()
{
    var fake = new FakeWindows(Snapshot() with { IsMinimized = true });
    var engine = new TrackingEngine(fake);
    var rule = Rule();
    engine.Tick([rule]);
    Equal(0, fake.Calls.Count);
    fake.Windows[0] = Snapshot();
    engine.Tick([rule]);
    Equal(1, fake.Calls.Count);
}

static void RetryFailure()
{
    var fake = new FakeWindows(Snapshot()) { Fail = true };
    var engine = new TrackingEngine(fake);
    var rule = Rule();
    Equal(true, engine.Tick([rule])[0].HasError);
    fake.Fail = false;
    Equal(false, engine.Tick([rule])[0].HasError);
    engine.Tick([rule]);
    Equal(2, fake.Calls.Count);
}

static void AsynchronousApply()
{
    var fake = new FakeWindows(Snapshot()) { DeferApply = true };
    var engine = new TrackingEngine(fake);
    var rule = Rule();
    engine.Tick([rule]);
    fake.Windows[0] = Snapshot() with { Bounds = rule.Bounds };
    engine.Tick([rule]);
    fake.Windows[0] = Snapshot();
    engine.Tick([rule]);
    Equal(1, fake.Calls.Count);
    engine.ResetRule(rule.Id);
    for (var i = 0; i < 3; i++)
        Equal(false, engine.Tick([rule])[0].HasError);
    Equal(true, engine.Tick([rule])[0].HasError);
    Equal(true, engine.Tick([rule])[0].HasError);
    Equal(4, fake.Calls.Count);
    fake.DeferApply = false;
    engine.ApplyNow(rule);
    Equal(false, engine.Tick([rule])[0].HasError);
    Equal(5, fake.Calls.Count);
}

static void NormalizedBounds()
{
    var fake = new FakeWindows(Snapshot()) { Normalized = new(0, 0, 900, 700) };
    var engine = new TrackingEngine(fake);
    var rule = Rule();
    engine.Tick([rule]);
    engine.Tick([rule]);
    fake.Windows[0] = Snapshot();
    engine.Tick([rule]);
    Equal(1, fake.Calls.Count);
    Equal(fake.Normalized, fake.Calls[0].Bounds);
}

static void DisabledAndPaused()
{
    var fake = new FakeWindows(Snapshot());
    var engine = new TrackingEngine(fake);
    var rule = Rule();
    engine.Tick([rule with { Enabled = false }]);
    engine.Tick([rule], paused: true);
    Equal(0, fake.Calls.Count);
    engine.Tick([rule]);
    Equal(1, fake.Calls.Count);
    fake.Windows.Clear();
    engine.Tick([rule], paused: true);
    fake.Windows.Add(Snapshot());
    engine.Tick([rule], paused: true);
    engine.Tick([rule]);
    Equal(2, fake.Calls.Count);
}

static void ResetAndConfiguration()
{
    var fake = new FakeWindows(Snapshot());
    var engine = new TrackingEngine(fake);
    var rule = Rule();
    engine.Tick([rule]);
    rule = rule with { X = -1500 };
    engine.Tick([rule]);
    fake.Windows[0] = Snapshot();
    engine.ResetRule(rule.Id);
    engine.Tick([rule]);
    Equal(3, fake.Calls.Count);
    engine.Tick([]);
    fake.Windows[0] = Snapshot();
    engine.Tick([rule]);
    Equal(4, fake.Calls.Count);
}

static void TitleMatching()
{
    var fake = new FakeWindows(Snapshot(title: "example"), Snapshot(2, title: "Prefix EXAMPLE - file"), Snapshot(3, title: "Elsewhere"));
    var engine = new TrackingEngine(fake);
    var rule = Rule();
    Equal(1, engine.Tick([rule])[0].MatchedCount);
    rule = rule with { MatchMode = TitleMatchMode.Contains };
    Equal(2, engine.Tick([rule])[0].MatchedCount);
    Equal(2, fake.Calls.Count);
    Equal(0, engine.Tick([rule with { Title = " " }])[0].MatchedCount);
}

static void ConflictOrder()
{
    var fake = new FakeWindows(Snapshot());
    var engine = new TrackingEngine(fake);
    var first = Rule();
    var second = Rule() with { X = 150, Mode = FollowMode.Continuous };
    var statuses = engine.Tick([first, second]);
    Equal(1, fake.Calls.Count);
    Equal(first.Bounds, fake.Calls[0].Bounds);
    Equal(true, statuses[1].Message.Contains("優先", StringComparison.Ordinal));
    engine.Tick([first, second]);
    Equal(1, fake.Calls.Count);
    engine.Tick([first with { Enabled = false }, second]);
    Equal(2, fake.Calls.Count);
    Equal(second.Bounds, fake.Calls[1].Bounds);
}

static void ImmediateApply()
{
    var fake = new FakeWindows(Snapshot());
    var engine = new TrackingEngine(fake);
    var rule = Rule();
    engine.Tick([rule]);
    fake.Windows[0] = Snapshot();
    engine.ApplyNow(rule);
    Equal(2, fake.Calls.Count);
    engine.Tick([rule]);
    Equal(2, fake.Calls.Count);
}

static void InvalidRules()
{
    var fake = new FakeWindows(Snapshot());
    var engine = new TrackingEngine(fake);
    foreach (var invalid in new[] { Rule() with { Width = 0 }, Rule() with { Mode = (FollowMode)100 }, Rule() with { Id = Guid.Empty } })
        Equal(true, engine.Tick([invalid])[0].HasError);
    Equal(0, fake.Calls.Count);
}

static void SettingsRoundTrip() => InTemporaryDirectory(directory =>
{
    var path = Path.Combine(directory, "nested", "settings.cfg");
    var store = new SettingsStore(path);
    Equal(0, store.Load().Rules.Count);
    Equal<string?>(null, store.LoadWarning);
    var rule = Rule() with { Title = "日本語のアプリ", Mode = FollowMode.Continuous, MatchMode = TitleMatchMode.Contains, Enabled = false };
    store.Save(new() { Rules = [rule] });
    Equal(rule, store.Load().Rules.Single());
    store.Save(new() { Rules = [rule with { X = 100 }] });
    Equal(rule with { X = 100 }, store.Load().Rules.Single());
    Equal(1, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
    Equal(false, File.Exists(path + ".bak"));
    Equal(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length);
});

static void CorruptSettings() => InTemporaryDirectory(directory =>
{
    var path = Path.Combine(directory, "settings.cfg");
    const string corrupt = "not a cfg";
    File.WriteAllText(path, corrupt);
    var store = new SettingsStore(path);
    Equal(0, store.Load().Rules.Count);
    Equal(true, !string.IsNullOrWhiteSpace(store.LoadWarning));
    Equal(corrupt, File.ReadAllText(path));
    store.Save(new() { Rules = [Rule()] });
    var savedCorruptPath = Directory.GetFiles(directory, "settings.cfg.corrupt.*").Single();
    Equal(corrupt, File.ReadAllText(savedCorruptPath));
    Equal(1, store.Load().Rules.Count);
});

static void TrayStartupSettings() => InTemporaryDirectory(directory =>
{
    var path = Path.Combine(directory, "settings.cfg");
    var store = new SettingsStore(path);
    foreach (var hidden in new[] { true, false, true })
    {
        store.Save(new() { StartInTray = hidden });
        Equal(hidden, store.Load().StartInTray);
        Equal(8L, new FileInfo(path).Length);
        var rules = Enumerable.Range(0, 64).Select(_ => Rule()).ToList();
        store.Save(new() { StartInTray = hidden, Rules = rules });
        var loaded = store.Load();
        Equal(hidden, loaded.StartInTray);
        Equal(true, rules.SequenceEqual(loaded.Rules));
    }
});

static void LegacySettingsMigration() => InTemporaryDirectory(directory =>
{
    var path = Path.Combine(directory, "settings.cfg");
    var store = new SettingsStore(path);
    File.WriteAllBytes(path, WithChecksum([(byte)'W', (byte)'P', 1, 0]));
    Equal(false, store.Load().StartInTray);
    Equal<string?>(null, store.LoadWarning);
    var rule = Rule() with { Title = "既存の設定", Mode = FollowMode.Continuous };
    store.Save(new() { Rules = [rule] });
    var legacyPayload = File.ReadAllBytes(path)[..^4];
    legacyPayload[2] = 1;
    legacyPayload[3] = 1; // v1 holds the count, not the v2 packed count/state.
    File.WriteAllBytes(path, WithChecksum(legacyPayload));
    var loaded = store.Load();
    Equal<string?>(null, store.LoadWarning);
    Equal(false, loaded.StartInTray);
    Equal(rule, loaded.Rules.Single());
    store.Save(loaded with { StartInTray = true });
    Equal(true, store.Load().StartInTray);
    Equal(rule, store.Load().Rules.Single());
});

static void InvalidSettings() => InTemporaryDirectory(directory =>
{
    var path = Path.Combine(directory, "settings.cfg");
    var store = new SettingsStore(path);
    var rule = Rule();
    store.Save(new() { Rules = [rule] });
    var original = Convert.ToHexString(File.ReadAllBytes(path));
    foreach (var settings in new AppSettings[]
    {
        new() { Rules = [rule, rule] },
        new() { Rules = [rule with { Width = -1 }] },
        new() { Rules = [rule with { Title = "  " }] },
        new() { Rules = [rule with { MatchMode = (TitleMatchMode)90 }] },
        new() { Rules = [rule with { Title = new string('a', 128 * 1024 + 1) }] },
        new() { Rules = Enumerable.Range(0, 10_001).Select(_ => Rule()).ToList() },
        new() { Rules = Enumerable.Range(0, 40).Select(_ => Rule() with { Title = new string('a', 128 * 1024) }).ToList() }
    })
    {
        Throws<InvalidDataException>(() => store.Save(settings));
        Equal(original, Convert.ToHexString(File.ReadAllBytes(path)));
    }
});

static void CorruptBinary() => InTemporaryDirectory(directory =>
{
    var path = Path.Combine(directory, "settings.cfg");
    var store = new SettingsStore(path);
    store.Save(new() { Rules = [Rule()] });
    var valid = File.ReadAllBytes(path);
    for (var length = 0; length < valid.Length; length++)
    {
        File.WriteAllBytes(path, valid[..length]);
        Equal(0, store.Load().Rules.Count);
        Equal(true, !string.IsNullOrWhiteSpace(store.LoadWarning));
    }
    for (var offset = 0; offset < valid.Length; offset++)
    {
        var altered = valid.ToArray();
        altered[offset] ^= 0x01;
        File.WriteAllBytes(path, altered);
        Equal(0, store.Load().Rules.Count);
        Equal(true, !string.IsNullOrWhiteSpace(store.LoadWarning));
    }
    var unknownVersion = valid.ToArray();
    unknownVersion[2] = 99;
    File.WriteAllBytes(path, unknownVersion);
    store.Load();
    Equal(true, store.LoadWarning!.Contains("バージョン", StringComparison.Ordinal));
    using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
        file.SetLength(4 * 1024 * 1024 + 1);
    Equal(0, store.Load().Rules.Count);
    Equal(true, !string.IsNullOrWhiteSpace(store.LoadWarning));
});

static void MalformedBinary() => InTemporaryDirectory(directory =>
{
    var path = Path.Combine(directory, "settings.cfg");
    var ruleBytes = new byte[23];
    Guid.NewGuid().TryWriteBytes(ruleBytes);
    ruleBytes[16] = 1; // Enabled, first appearance, exact title.
    ruleBytes[19] = 1; // Width.
    ruleBytes[20] = 1; // Height.
    ruleBytes[21] = 1; // One title byte.
    ruleBytes[22] = (byte)'A';
    List<byte[]> malformed =
    [
        [(byte)'W', (byte)'P', 1, 0x90, 0x4e], // Huge count with no records.
        [(byte)'W', (byte)'P', 1, 0xff, 0xff, 0xff, 0xff, 0x1f], // Uint overflow.
        [(byte)'W', (byte)'P', 1, 0x80, 0], // Non-canonical varint.
        [(byte)'W', (byte)'P', 1, 0, 1], // Trailing data.
        [(byte)'W', (byte)'P', 1, 2, .. ruleBytes, .. ruleBytes] // Duplicate IDs.
    ];
    foreach (var (offset, value) in new (int, byte)[] { (16, 128), (19, 0), (20, 0), (21, 127), (22, 0xff), (22, (byte)' ') })
    {
        var altered = ruleBytes.ToArray();
        altered[offset] = value;
        malformed.Add([(byte)'W', (byte)'P', 1, 1, .. altered]);
    }
    foreach (var payload in malformed)
    {
        File.WriteAllBytes(path, WithChecksum(payload));
        var store = new SettingsStore(path);
        Equal(0, store.Load().Rules.Count);
        Equal(true, !string.IsNullOrWhiteSpace(store.LoadWarning));
    }
});

static byte[] WithChecksum(byte[] payload)
{
    var checksum = uint.MaxValue;
    foreach (var value in payload)
    {
        checksum ^= value;
        for (var bit = 0; bit < 8; bit++)
            checksum = (checksum >> 1) ^ (0xEDB88320u & unchecked((uint)-(int)(checksum & 1)));
    }
    return [.. payload, .. BitConverter.GetBytes(~checksum)];
}

static void IntegerAndFlagExtremes() => InTemporaryDirectory(directory =>
{
    var store = new SettingsStore(Path.Combine(directory, "settings.cfg"));
    foreach (var coordinate in new[] { int.MinValue, -16384, -129, -128, -1, 0, 1, 127, 128, 16384, int.MaxValue })
        for (var flags = 0; flags < 8; flags++)
        {
            var rule = Rule() with
            {
                X = coordinate, Y = coordinate, Width = int.MaxValue, Height = 1,
                Enabled = (flags & 1) != 0, Mode = (FollowMode)((flags >> 1) & 1), MatchMode = (TitleMatchMode)((flags >> 2) & 1)
            };
            store.Save(new() { Rules = [rule] });
            Equal(rule, store.Load().Rules.Single());
        }
});

static void CompactSizeExamples() => InTemporaryDirectory(directory =>
{
    var path = Path.Combine(directory, "settings.cfg");
    var store = new SettingsStore(path);
    foreach (var count in new[] { 0, 1, 10 })
    {
        var rules = Enumerable.Range(0, count).Select(_ => Rule() with { Title = "メモ帳 — 買い物リスト" }).ToList();
        store.Save(new() { Rules = rules });
        Equal(count, store.Load().Rules.Count);
        var size = new FileInfo(path).Length;
        if (count == 0) Equal(8L, size);
        Console.WriteLine($"SIZE {count} Japanese rules: {size} bytes");
    }
    Equal(1, Directory.GetFiles(directory).Length);
});

static void AlignEdges()
{
    var current = new WindowBounds(240, 180, 640, 480);
    var work = new WindowBounds(0, 0, 1920, 1040);
    foreach (var (edge, expected) in new[]
    {
        (WindowEdge.Up, new WindowBounds(240, 0, 640, 480)),
        (WindowEdge.Down, new WindowBounds(240, 560, 640, 480)),
        (WindowEdge.Left, new WindowBounds(0, 180, 640, 480)),
        (WindowEdge.Right, new WindowBounds(1280, 180, 640, 480))
    })
    {
        Equal(true, WindowAlignment.TryAlign(current, current, work, edge, out var actual, out var error));
        Equal(expected, actual);
        Equal<string?>(null, error);
    }
}

static void AlignCorners()
{
    var work = new WindowBounds(0, 0, 1920, 1040);
    foreach (var vertical in new[] { WindowEdge.Up, WindowEdge.Down })
        foreach (var horizontal in new[] { WindowEdge.Left, WindowEdge.Right })
            foreach (var reverse in new[] { false, true })
            {
                var current = new WindowBounds(240, 180, 640, 480);
                var edges = reverse ? new[] { horizontal, vertical } : [vertical, horizontal];
                foreach (var edge in edges)
                {
                    Equal(true, WindowAlignment.TryAlign(current, current, work, edge, out var next, out _));
                    current = next;
                }
                Equal(new WindowBounds(horizontal == WindowEdge.Left ? 0 : 1280,
                    vertical == WindowEdge.Up ? 0 : 560, 640, 480), current);
            }
}

static void AlignNegativeMonitor()
{
    var current = new WindowBounds(-1600, -700, 900, 600);
    var work = new WindowBounds(-1920, -1080, 1920, 1040);
    Equal(true, WindowAlignment.TryAlign(current, current, work, WindowEdge.Up, out var up, out _));
    Equal(true, WindowAlignment.TryAlign(up, up, work, WindowEdge.Left, out var corner, out _));
    Equal(new WindowBounds(-1920, -1080, 900, 600), corner);
    Equal(true, WindowAlignment.TryAlign(corner, corner, work, WindowEdge.Down, out var down, out _));
    Equal(true, WindowAlignment.TryAlign(down, down, work, WindowEdge.Right, out corner, out _));
    Equal(new WindowBounds(-900, -640, 900, 600), corner);
}

static void AlignOffScreen()
{
    var work = new WindowBounds(0, 0, 1920, 1040);
    var current = new WindowBounds(-200, 950, 640, 480);
    Equal(true, WindowAlignment.TryAlign(current, current, work, WindowEdge.Up, out var aligned, out _));
    Equal(new WindowBounds(0, 0, 640, 480), aligned);
    Equal(true, WindowAlignment.TryAlign(current, current, work, WindowEdge.Right, out aligned, out _));
    Equal(new WindowBounds(1280, 560, 640, 480), aligned);
    current = new WindowBounds(2000, -400, 640, 480);
    Equal(true, WindowAlignment.TryAlign(current, current, work, WindowEdge.Down, out aligned, out _));
    Equal(new WindowBounds(1280, 560, 640, 480), aligned);
    Equal(true, WindowAlignment.TryAlign(current, current, work, WindowEdge.Left, out aligned, out _));
    Equal(new WindowBounds(0, 0, 640, 480), aligned);
}

static void AlignInvisibleBorders()
{
    var outer = new WindowBounds(100, 200, 816, 640);
    var visible = new WindowBounds(108, 204, 800, 628);
    var work = new WindowBounds(0, 0, 1920, 1040);
    foreach (var (edge, expected) in new[]
    {
        (WindowEdge.Up, new WindowBounds(100, -4, 816, 640)),
        (WindowEdge.Down, new WindowBounds(100, 408, 816, 640)),
        (WindowEdge.Left, new WindowBounds(-8, 200, 816, 640)),
        (WindowEdge.Right, new WindowBounds(1112, 200, 816, 640))
    })
    {
        Equal(true, WindowAlignment.TryAlign(outer, visible, work, edge, out var actual, out _));
        Equal(expected, actual);
    }

    Equal(true, WindowAlignment.TryAlign(outer, visible, work, WindowEdge.Up, out var up, out _));
    var movedVisible = visible with { X = visible.X + up.X - outer.X, Y = visible.Y + up.Y - outer.Y };
    Equal(true, WindowAlignment.TryAlign(up, movedVisible, work, WindowEdge.Left, out var corner, out _));
    Equal(new WindowBounds(-8, -4, 816, 640), corner);
    // The outer resize borders may be larger than the work area when the
    // complete visible frame fits exactly; that does not resize the window.
    var filledOuter = new WindowBounds(-8, -4, 1936, 1052);
    Equal(true, WindowAlignment.TryAlign(filledOuter, work, work, WindowEdge.Right, out var filled, out _));
    Equal(filledOuter, filled);
}

static void AlignWorkArea()
{
    var current = new WindowBounds(240, 180, 640, 480);
    var work = new WindowBounds(60, 48, 1860, 992);
    Equal(true, WindowAlignment.TryAlign(current, current, work, WindowEdge.Up, out var up, out _));
    Equal(true, WindowAlignment.TryAlign(up, up, work, WindowEdge.Left, out var corner, out _));
    Equal(new WindowBounds(60, 48, 640, 480), corner);
    Equal(true, WindowAlignment.TryAlign(corner, corner, work, WindowEdge.Down, out var down, out _));
    Equal(true, WindowAlignment.TryAlign(down, down, work, WindowEdge.Right, out corner, out _));
    Equal(new WindowBounds(1280, 560, 640, 480), corner);
}

static void AlignOversized()
{
    var work = new WindowBounds(0, 0, 1920, 1040);
    foreach (var current in new[] { new WindowBounds(100, 100, 1921, 600), new WindowBounds(100, 100, 900, 1041) })
        foreach (var edge in Enum.GetValues<WindowEdge>())
        {
            Equal(false, WindowAlignment.TryAlign(current, current, work, edge, out var aligned, out var error));
            Equal(current, aligned);
            Equal(true, error!.Contains("サイズを保ったまま", StringComparison.Ordinal));
        }
}

static void AlignInvalid()
{
    var valid = new WindowBounds(0, 0, 600, 400);
    var work = new WindowBounds(0, 0, 1920, 1040);
    foreach (var invalid in new[]
    {
        valid with { Width = 0 }, valid with { Height = -1 },
        valid with { X = int.MaxValue }, valid with { Y = int.MaxValue }
    })
    {
        Equal(false, WindowAlignment.TryAlign(invalid, invalid, work, WindowEdge.Left, out _, out _));
        Equal(false, WindowAlignment.TryAlign(valid, valid, invalid, WindowEdge.Left, out _, out _));
        Equal(false, WindowAlignment.TryAlign(valid, invalid, work, WindowEdge.Left, out _, out _));
    }
    Equal(false, WindowAlignment.TryAlign(valid, valid, work, (WindowEdge)99, out _, out _));
    Equal(false, WindowAlignment.TryAlign(valid, valid with { X = -1 }, work, WindowEdge.Left, out _, out _));
    Equal(false, WindowAlignment.TryAlign(valid, valid with { Width = 601 }, work, WindowEdge.Right, out _, out _));

    var outer = new WindowBounds(0, 0, 600, 400);
    var visible = new WindowBounds(8, 0, 584, 392);
    var extremeWork = new WindowBounds(int.MinValue, 0, 1920, 1040);
    Equal(false, WindowAlignment.TryAlign(outer, visible, extremeWork, WindowEdge.Left, out var failed, out var error));
    Equal(outer, failed);
    Equal(true, !string.IsNullOrWhiteSpace(error));
    extremeWork = new WindowBounds(int.MaxValue - 1920, 0, 1920, 1040);
    Equal(false, WindowAlignment.TryAlign(outer, visible, extremeWork, WindowEdge.Right, out _, out _));
}

static void InTemporaryDirectory(Action<string> action)
{
    var directory = Path.Combine(Path.GetTempPath(), "WindowPosition.Tests." + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try { action(directory); }
    finally { Directory.Delete(directory, recursive: true); }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected exception {typeof(T).Name}.");
}

sealed class FakeWindows(params WindowSnapshot[] snapshots) : IWindowSystem
{
    public List<WindowSnapshot> Windows { get; } = [.. snapshots];
    public List<(nint Handle, WindowBounds Bounds)> Calls { get; } = [];
    public bool Fail { get; set; }
    public bool DeferApply { get; set; }
    public WindowBounds? Normalized { get; set; }
    public IReadOnlyList<WindowSnapshot> EnumerateWindows() => Windows.ToArray();
    public WindowBounds NormalizeBounds(WindowBounds bounds) => Normalized ?? bounds;

    public bool TrySetBounds(nint handle, WindowBounds bounds, out string? error)
    {
        Calls.Add((handle, bounds));
        if (Fail)
        {
            error = "Access denied";
            return false;
        }
        if (!DeferApply)
        {
            var index = Windows.FindIndex(window => window.Handle == handle);
            Windows[index] = Windows[index] with { Bounds = bounds, IsMaximized = false };
        }
        error = null;
        return true;
    }
}
