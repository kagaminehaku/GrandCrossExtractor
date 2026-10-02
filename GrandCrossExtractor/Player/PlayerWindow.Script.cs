// Story player: runs the game flow and the scenario commands. Command meanings come from
// START.SCN and EFCLIB.SCN; see docs/engine-notes.md, section 8.

using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GrandCrossExtractor.Player;

public partial class PlayerWindow
{
    // Script variables (_D710 ...), kept for the whole run
    private readonly Dictionary<int, int> m_vars = new();

    // Text speed of the default configuration: "_w" = b[3] (2) * 18 ms per character
    private const int CharacterMs = 36;

    #region Flow

    // Where the story is, for saves: routes played, scenario file and message number in it
    private int m_played;
    private string m_file = "";
    private int m_messageIndex;
    private string m_lastText = "";

    // Loading a save: messages before this one are passed silently (-1 = not loading)
    private int m_restoreTo = -1;

    private async Task RunFlowAsync(string? startFile, int initialPlayed = 0)
    {
        int all = (1 << m_flow.Routes.Count) - 1;
        int played = m_played = initialPlayed;
        bool started = startFile == null;

        foreach (var file in m_flow.Opening)
        {
            started |= Same(file, startFile);
            if (started)
                await PlayScriptAsync(file);
        }

        if (!started)
        {
            for (int r = 0; r < m_flow.Routes.Count && !started; r++)
            {
                if (RouteContains(m_flow.Routes[r], startFile!))
                {
                    m_played = played |= 1 << r;
                    started = true;
                    await PlayRouteAsync(m_flow.Routes[r], startFile);
                }
            }
        }
        if (!started && m_flow.Ending.Any(f => Same(f, startFile)))
            m_played = played = all;

        while (played != all)
        {
            int route = await ChooseAsync(m_flow.Routes.Select(r => r.Title).ToList(), m_flow.MenuButtonSlot, played);
            m_played = played |= 1 << route;
            await PlayRouteAsync(m_flow.Routes[route], null);
        }

        foreach (var file in m_flow.Ending)
        {
            started |= Same(file, startFile);
            if (started)
                await PlayScriptAsync(file);
        }
    }

    private static bool Same(string a, string? b) => b != null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static bool RouteContains(Route route, string file) =>
        route.Steps.Any(s => s is PlayStep p ? Same(p.File, file) : s is ChoiceStep c && c.Branches.Any(b => b.Any(f => Same(f, file))));

    private async Task PlayRouteAsync(Route route, string? from)
    {
        bool on = from == null;
        foreach (var step in route.Steps)
        {
            if (step is PlayStep play)
            {
                on |= Same(play.File, from);
                if (on)
                    await PlayScriptAsync(play.File);
            }
            else if (step is ChoiceStep choice)
            {
                int branch;
                int skipTo = 0;
                if (on)
                {
                    branch = await ChooseAsync(choice.Options);
                }
                else
                {
                    branch = Array.FindIndex(choice.Branches, b => b.Any(f => Same(f, from)));
                    if (branch < 0)
                        continue;
                    skipTo = Array.FindIndex(choice.Branches[branch], f => Same(f, from));
                    on = true;
                }
                foreach (var file in choice.Branches[branch].Skip(skipTo))
                    await PlayScriptAsync(file);
            }
        }
    }

    private async Task PlayScriptAsync(string file)
    {
        m_file = file;
        m_messageIndex = 0;
        var script = await Task.Run(() => m_data.LoadScript(file)).WaitAsync(m_token)
            ?? throw new InvalidOperationException($"The scenario file {file} is missing.");
        await ExecuteAsync(script);
    }

    #endregion

    #region Commands

    private async Task ExecuteAsync(ScenarioScript script)
    {
        var lines = script.Lines;
        int? eventBlockEnd = null;

        for (int pc = 0; pc < lines.Count; pc++)
        {
            m_token.ThrowIfCancellationRequested();

            // Skipping inside an $EVENT_BLOCK jumps to its end label
            if (eventBlockEnd is int label && Skipping && script.Labels.TryGetValue(label, out int target) && target > pc)
            {
                pc = target;
                eventBlockEnd = null;
                m_stage.FinishAll();
            }

            var line = lines[pc];
            if (line.Text != null)
            {
                await ShowMessageAsync(line);
                continue;
            }

            switch (line.Command)
            {
                case "L_BG":
                    // $L_BG,file,reset,x,y,zoom: reset 0 clears planes 1-9
                    if (line.IntArg(1) == 0)
                        m_stage.ClearCharacters();
                    m_stage.SetPlane(0, await LoadImageAsync(line.Arg(0)), line.IntArg(2), line.IntArg(3));
                    break;

                case "L_CHR":
                case "L_MONT":
                    await LoadPlaneAsync(line.IntArg(0), line);
                    break;

                case "DRAW_EX":
                {
                    // $DRAW_EX,kind,rule,ms,hide window: the engine waits for the transition
                    var kind = line.IntArg(0) switch
                    {
                        1 => Transition.Cut,
                        2 or 37 or 48 => Transition.RuleBrightFirst,
                        47 => Transition.RuleDarkFirst,
                        _ => Transition.CrossFade,
                    };
                    BitmapSource? rule = null;
                    if (kind is Transition.RuleBrightFirst or Transition.RuleDarkFirst && !Skipping)
                        rule = (await LoadImageAsync(line.Arg(1)))?.Bitmap;
                    if (line.IntArg(3) != 0)
                        HideMessageWindow();
                    await WaitSkippableAsync(m_stage.DrawEx(kind, Skipping ? 0 : line.IntArg(2), rule));
                    break;
                }

                case "DRAW":
                    m_stage.Draw();
                    break;

                case "A_CHR":
                    await AnimateCharacterAsync(line);
                    break;

                case "WAITA":
                    await WaitSkippableAsync(m_stage.WhenIdle());
                    break;

                case "WAIT":
                    if (!Skipping)
                        await WaitSkippableAsync(Task.Delay(line.IntArg(0), m_token));
                    break;

                case "WINDOW":
                    if (line.IntArg(0) == 0)
                        HideMessageWindow();
                    else
                        ShowMessageWindow();
                    break;

                case "EFECT":
                    if (!Skipping)
                        await WaitSkippableAsync(ScreenEffect(line.IntArg(0)));
                    break;

                case "MUSIC":
                    // $MUSIC,file,loop,fade-in ms
                    if (line.Arg(0).Length == 0)
                        m_audio.Stop(AudioEngine.Music);
                    else if (await Task.Run(() => m_data.ReadAudio(line.Arg(0))) is { } music)
                        m_audio.Play(AudioEngine.Music, music, line.IntArg(1) == 0 ? 1 : 0, line.IntArg(2));
                    break;

                case "MUSIC_FADE":
                    m_audio.Stop(AudioEngine.Music, line.IntArg(0, 1000));
                    break;

                case "SE":
                    await SoundEffectAsync(line);
                    break;

                case "SE_FADE":
                    m_audio.Stop(AudioEngine.Effect(line.IntArg(1)), line.IntArg(0));
                    break;

                case "VOICE":
                    m_audio.Stop(AudioEngine.Voice);
                    m_pendingVoice = line.Arg(0);
                    if (!Skipping && await Task.Run(() => m_data.ReadAudio(line.Arg(0))) is { } voice)
                        m_audio.Play(AudioEngine.Voice, voice, 1);
                    break;

                case "L_MOVIE":
                {
                    string? path = line.Arg(1).Length > 0 ? m_data.LooseFile(line.Arg(1)) : null;
                    m_stage.PlayMovie(line.IntArg(0), path, line.IntArg(2) != 0, m_audio.EffectVolume);
                    break;
                }

                case "WAIT_L_MOVIE":
                    if (!Skipping)
                        await WaitSkippableAsync(m_stage.WhenMovieEnds());
                    break;

                case "EX":
                    await ExtendedAsync(line);
                    break;

                case "LABEL":
                    if (eventBlockEnd == line.IntArg(0))
                        eventBlockEnd = null;
                    break;

                case "CJUMP":
                    if (Condition(line.Arg(0)) && script.Labels.TryGetValue(line.IntArg(1), out int jump))
                        pc = jump - 1;
                    break;

                case "EVENT_BLOCK":
                    eventBlockEnd = line.IntArg(1);
                    break;

                case "PRELOAD":
                {
                    string file = line.Arg(0);
                    if (file.EndsWith(".S25", StringComparison.OrdinalIgnoreCase))
                        _ = Task.Run(() => m_data.LoadFrames(file));
                    break;
                }
            }
        }
        // A scenario file ends with its own fade-out; whatever is still playing stops here
        await WaitSkippableAsync(m_stage.WhenIdle());
    }

    /// <summary>
    /// $L_CHR,plane,file,x,y,type[,m,base,layer1,...] and $L_MONT,plane,file,x,y,?,m|M,...:
    /// "m" lists the slots (value v at position k = slot k*100+v, -1 = off), "M" gives an
    /// expression code from MONTBL.BIN (the file may then be left out).
    /// </summary>
    private async Task LoadPlaneAsync(int plane, ScriptLine line)
    {
        string file = line.Arg(1);
        double x = line.IntArg(2), y = line.IntArg(3);
        int marker = Array.FindIndex(line.Args, 2, a => a is "m" or "M");
        int[]? slots = null;
        bool expression = false;

        if (marker >= 0 && line.Args[marker] == "M")
        {
            if (m_data.GetMontage(line.IntArg(marker + 1)) is { } montage)
            {
                if (file.Length == 0)
                    file = montage.File;
                slots = montage.Slots;
                expression = true;
            }
        }
        else if (marker >= 0)
        {
            var list = new List<int>();
            for (int k = 0; marker + 1 + k < line.Args.Length; k++)
                if (int.TryParse(line.Args[marker + 1 + k], out int v) && v >= 0)
                    list.Add(k * 100 + v);
            slots = list.ToArray();
        }

        if (file.Length == 0)
        {
            m_stage.SetPlane(plane, null);
            return;
        }
        var image = await Task.Run(() => m_data.LoadImage(file, slots)).WaitAsync(m_token);
        if (image == null)
        {
            m_stage.SetPlane(plane, null);
            return;
        }
        // Both go through the action queue (function 304 / 10001): the 5th argument is the plane
        // transition the next $DRAW runs, 0 = a 500 ms cross-fade
        if (expression)
            m_stage.ChangePicture(plane, image, x, y);
        else
            m_stage.SetPlane(plane, image, x, y);
        PlaneTransition(plane, line.IntArg(4), x, y, -1, false);
    }

    private async Task<StageImage?> LoadImageAsync(string file) =>
        file.Length == 0 ? null : await Task.Run(() => m_data.LoadImage(file)).WaitAsync(m_token);

    // Function 306 of START.SCN: plane transition type -> kind (table at 0x680F0)
    private static readonly int[] s_transitionKinds = { 0, 3, 3, 3, 4, 4, 4, 5, 5, 5, 6, 6, 6, 2, 3, 3, 4, 5, 6, 1, 7, 7, 7, 7, 8, 8, 8, 8, 5, 7 };

    // Where a slide starts (slide in) or ends (slide out); types 14, 28 and 29 move from the current position
    private static readonly Dictionary<int, (int X, int Y)> s_slideOffsets = new()
    {
        [1] = (0, 800), [2] = (-800, 0), [3] = (800, 0), [15] = (0, -800),
        [4] = (0, 800), [5] = (800, 0), [6] = (-800, 0), [16] = (0, -800),
        [7] = (0, 800), [8] = (-800, 0), [9] = (800, 0), [17] = (0, -800),
        [10] = (0, 800), [11] = (800, 0), [12] = (-800, 0), [18] = (0, -800),
        [20] = (0, 800), [21] = (0, -800), [22] = (-800, 0), [23] = (800, 0),
        [24] = (0, -800), [25] = (0, 800), [26] = (-800, 0), [27] = (800, 0),
    };

    /// <summary>
    /// Function 306: a plane transition of <paramref name="type"/> (0-29) to (x, y) over
    /// <paramref name="ms"/> (-1 = the type's default). 0 cross-fade, 19 fade in, 13 fade out and
    /// remove, others slide in / out or move with easing 1 (linear), 3 (slow end) or 2 (slow start).
    /// </summary>
    private void PlaneTransition(int plane, int type, double x, double y, int ms, bool background)
    {
        if (type < 0 || type >= s_transitionKinds.Length)
            type = 0;
        int kind = s_transitionKinds[type];
        bool fromCurrent = type is 14 or 28 or 29;
        double Time(int fallback) => ms >= 0 ? ms : fallback;
        int easing = kind switch { 3 or 4 => 1, 5 or 6 => 3, _ => 2 };
        s_slideOffsets.TryGetValue(type, out var offset);

        switch (kind)
        {
            case 0:
                m_stage.QueueCrossFade(plane, Time(500), background);
                break;
            case 1:
                m_stage.QueueFadeIn(plane, Time(1000), background);
                break;
            case 2:
                m_stage.QueueFadeOut(plane, Time(1000), background);
                break;
            case 3 or 5 or 7:   // slide in, or move from the current position
                m_stage.QueueMove(plane, fromCurrent ? null : offset.X, fromCurrent ? null : offset.Y, x, y, easing,
                                  Time(fromCurrent ? 500 : 1000), background, removeAtEnd: false);
                break;
            case 4 or 6 or 8:   // slide out, then remove the plane
                m_stage.QueueMove(plane, x, y, offset.X, offset.Y, easing, Time(1000), background, removeAtEnd: true);
                break;
        }
    }

    /// <summary>
    /// $A_CHR,code,plane,...: plane animations, started by the next $DRAW. The last argument
    /// (wf) of most codes makes a background animation that $WAITA does not wait for.
    /// </summary>
    private async Task AnimateCharacterAsync(ScriptLine line)
    {
        int code = line.IntArg(0), plane = line.IntArg(1);
        int P(int i) => line.IntArg(2 + i);
        switch (code)
        {
            case 0:             // stop the loop at the end of its cycle
            case 9:             // stop the loop now
                m_stage.QueueStopLoop(plane, code == 9);
                break;
            case >= 1 and <= 6: // loop: cycles (0 = forever), amplitude, period
                m_stage.QueueLoop(plane, code, P(0), P(1), P(2));
                break;
            case 40:            // screen area the plane is drawn into
                m_stage.SetViewTarget(plane, new Rect(P(0), P(1), P(2), P(3)));
                break;
            case 41:            // part of the plane shown in that area
                m_stage.SetViewSource(plane, new Rect(P(0), P(1), P(2), P(3)));
                break;
            case >= 42 and <= 44:   // pan / zoom: x, y, w, h, ms, wf; easing 1-3
                m_stage.QueueView(plane, new Rect(P(0), P(1), P(2), P(3)), code - 41, P(4), P(5) != 0);
                break;
            case >= 60 and <= 63:   // through a rule mask: rule, ms; 60/62 appear, 61/63 disappear, 62/63 reversed
                if (await LoadImageAsync(line.Arg(2)) is { } rule)
                    m_stage.QueueRuleFade(plane, rule.Bitmap, code % 2 == 0, code >= 62, line.IntArg(3), false);
                break;
            case 90:            // play sound channel n at every cycle of the loop (footsteps)
            {
                int channel = P(0);
                m_stage.QueueCycleAction(plane, () => PlaySoundSlot(channel));
                break;
            }
            case 91:
                m_stage.QueueCycleAction(plane, null);
                break;
            case >= 100 and <= 129: // function 306: x, y, ms, wf
                PlaneTransition(plane, code - 100, P(0), P(1), P(2), P(3) != 0);
                break;
            case 150:           // fade out (then remove), fade in, cross-fade: ms, wf
            case 151:
            case 152:
                if (m_stage.TryGetPosition(plane, out double x, out double y))
                    PlaneTransition(plane, code == 150 ? 13 : code == 151 ? 19 : 0, x, y, P(0), P(1) != 0);
                break;
        }
    }

    /// <summary>$EFECT,n (function 217 of START.SCN, effects of EFCLIB.SCN). The engine waits for it.</summary>
    private Task ScreenEffect(int effect)
    {
        // EFCLIB 35 zoom pulse sizes: (pixels a side per step x, y, rounds)
        (int, int, int)[] pulses = { (8, 6, 2), (16, 12, 2), (32, 24, 2), (4, 3, 2), (8, 6, 1), (16, 12, 1), (32, 24, 1), (4, 3, 1) };
        int[] pulseOf = { 1, 2, 0, 3, 5, 6, 4, 7 };   // $EFECT 8-15
        switch (effect)
        {
            case 0: return m_stage.Shake(16);
            case 1: return m_stage.Shake(32);
            case 2: return m_stage.Shake(8);
            case >= 8 and <= 15:
            {
                var (w, h, rounds) = pulses[pulseOf[effect - 8]];
                return m_stage.ZoomPulse(w, h, rounds);
            }
            case 3: return m_stage.Flash(Colors.White, 300, fade: true);
            case 4: return m_stage.Flash(Colors.White, 50, fade: false);
            case 5: return m_stage.Flash(Colors.Red, 50, fade: false);
            case 6: return m_stage.Flash(Colors.White, 1000, fade: true);
            default: return Task.CompletedTask;
        }
    }

    // Sounds loaded into the effect channels ($SE), replayed by A_CHR 90
    private readonly Dictionary<int, byte[]> m_soundSlots = new();

    /// <summary>
    /// $SE,file,mode,channel: mode 0 plays once, 1 loops, 2 plays once and waits, 3 only loads
    /// the sound (for A_CHR 90). No file stops the channel.
    /// </summary>
    private async Task SoundEffectAsync(ScriptLine line)
    {
        int mode = line.IntArg(1), channel = line.IntArg(2);
        string name = AudioEngine.Effect(channel);
        if (line.Arg(0).Length == 0)
        {
            m_audio.Stop(name);
            return;
        }
        if (await Task.Run(() => m_data.ReadAudio(line.Arg(0))) is not { } sound)
            return;
        m_soundSlots[channel] = sound;
        if (mode == 3 || (Skipping && mode != 1))
            return;
        m_audio.Play(name, sound, mode == 1 ? 0 : 1);
        if (mode == 2)
            await WaitSkippableAsync(WhenSoundEnds(name));
    }

    private void PlaySoundSlot(int channel)
    {
        if (!Skipping && m_soundSlots.TryGetValue(channel, out var sound))
            m_audio.Play(AudioEngine.Effect(channel), sound, 1);
    }

    private async Task WhenSoundEnds(string channel)
    {
        while (m_audio.IsPlaying(channel))
            await Task.Delay(50, m_token);
    }

    /// <summary>$EX,group,...: background scroll (9), variables (10) and key waits (2).</summary>
    private async Task ExtendedAsync(ScriptLine line)
    {
        switch (line.IntArg(0))
        {
            case 9:
                switch (line.IntArg(1))
                {
                    case 0: m_stage.ScrollInit(line.IntArg(3)); break;             // count, width
                    case 1:                                                          // slot, file
                        if (await LoadImageAsync(line.Arg(3)) is { } picture)
                            m_stage.ScrollImage(picture);
                        break;
                    case 2: m_stage.ScrollStart(line.IntArg(2, 10)); break;          // pixels per second
                    case 3: m_stage.ScrollStart(0); break;
                    case 4: m_stage.ScrollStop(); break;
                }
                break;
            case 10 when line.IntArg(1) == 2:
                m_vars[line.IntArg(2)] = line.IntArg(3);
                break;
            case 2:
                if (!Skipping)
                    await NextClick().WaitAsync(m_token);
                break;
        }
    }

    /// <summary>"_D710==0" style conditions of $CJUMP.</summary>
    private bool Condition(string text)
    {
        var m = Regex.Match(text, @"^_D(\d+)\s*(==|!=|>=|<=|>|<)\s*(-?\d+)$");
        if (!m.Success)
            return false;
        int value = m_vars.GetValueOrDefault(int.Parse(m.Groups[1].Value));
        int other = int.Parse(m.Groups[3].Value);
        return m.Groups[2].Value switch
        {
            "==" => value == other,
            "!=" => value != other,
            ">=" => value >= other,
            "<=" => value <= other,
            ">" => value > other,
            _ => value < other,
        };
    }

    /// <summary>Waits for <paramref name="task"/>; a click (or skipping) ends the animations at once.</summary>
    private async Task WaitSkippableAsync(Task task)
    {
        if (task.IsCompleted)
            return;
        if (Skipping)
        {
            m_stage.FinishAll();
            return;
        }
        var click = NextClick();
        var done = await Task.WhenAny(task, click).WaitAsync(m_token);
        if (done != task)
            m_stage.FinishAll();
    }

    #endregion

    #region Messages

    // Voice file of the message being shown, for the backlog
    private string? m_pendingVoice;

    private async Task ShowMessageAsync(ScriptLine line)
    {
        string text = line.Text!;
        string? voice = m_pendingVoice;
        m_pendingVoice = null;
        AddLog(line.Speaker ?? "", ScenarioScript.DisplayText(text), voice);

        // Loading a save: pass the messages before the saved one
        int index = m_messageIndex++;
        m_lastText = text;
        if (m_restoreTo >= 0)
        {
            if (index < m_restoreTo)
                return;
            m_restoreTo = -1;
            UpdateModeButtons();
        }

        SetSpeaker(line.Speaker);
        ShowMessageWindow();
        TxtNext.Visibility = Visibility.Collapsed;

        if (Skipping)
        {
            SetMessageText(text, text.Length);
            await Task.Delay(15, m_token);
            return;
        }

        // Type the text out; a click shows the rest at once
        var click = NextClick();
        for (int shown = 1; shown < text.Length; shown++)
        {
            SetMessageText(text, shown);
            if (await Task.WhenAny(Task.Delay(CharacterMs, m_token), click) == click || Skipping)
                break;
            m_token.ThrowIfCancellationRequested();
        }
        SetMessageText(text, text.Length);
        m_token.ThrowIfCancellationRequested();
        TxtNext.Visibility = Visibility.Visible;

        // Wait for a click; in auto mode go on once the voice has ended and the text had time to be read
        while (!Skipping)
        {
            click = NextClick();
            if (!m_auto)
            {
                m_modeChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (await Task.WhenAny(click, m_modeChanged.Task).WaitAsync(m_token) == click)
                    break;
                continue;
            }
            while (m_audio.IsPlaying(AudioEngine.Voice) && m_auto && !click.IsCompleted)
                await Task.Delay(100, m_token);
            if (click.IsCompleted)
                break;
            var read = Task.Delay(voice != null ? 600 : 900 + 60 * text.Length, m_token);
            if (await Task.WhenAny(read, click) == click || m_auto)
                break;
        }

        TxtNext.Visibility = Visibility.Collapsed;
        if (!Skipping)
            m_audio.Stop(AudioEngine.Voice);
    }

    #endregion
}
