// Story player: runs the game flow and the scenario commands. Command meanings are in
// docs/engine-notes.md; those marked "guess" there are approximated here.

using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace GrandCrossExtractor.Player;

public partial class PlayerWindow
{
    // Script variables (_D710 ...), kept for the whole run
    private readonly Dictionary<int, int> m_vars = new();

    // Cross-fade time asked for by "$A_CHR,152,..." for the next $DRAW
    private double m_drawFade;

    private const int CharacterMs = 28;

    #region Flow

    private async Task RunFlowAsync(string? startFile)
    {
        int all = (1 << m_flow.Routes.Count) - 1;
        int played = 0;
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
                    played |= 1 << r;
                    started = true;
                    await PlayRouteAsync(m_flow.Routes[r], startFile);
                }
            }
        }
        if (!started && m_flow.Ending.Any(f => Same(f, startFile)))
            played = all;

        while (played != all)
        {
            int route = await ChooseAsync(m_flow.Routes.Select(r => r.Title).ToList(), m_flow.MenuButtonSlot, played);
            played |= 1 << route;
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
                    m_stage.ClearCharacters();
                    m_stage.SetPlane(0, await LoadImageAsync(line.Arg(0)), line.IntArg(1), line.IntArg(2));
                    break;

                case "L_CHR":
                    await LoadPlaneAsync(line.IntArg(0), line, 1);
                    break;

                case "L_MONT":
                    await LoadPlaneAsync(line.IntArg(0), line, 1);
                    break;

                case "DRAW_EX":
                {
                    BitmapSource? rule = null;
                    if (line.Arg(1).Length > 0 && !Skipping)
                        rule = (await LoadImageAsync(line.Arg(1)))?.Bitmap;
                    double ms = Skipping ? 0 : line.IntArg(2);
                    m_drawFade = 0;
                    var done = m_stage.Commit(ms, rule);
                    if (line.IntArg(3) != 0)
                        await WaitSkippableAsync(done);
                    break;
                }

                case "DRAW":
                {
                    var done = m_stage.Commit(Skipping ? 0 : m_drawFade);
                    m_drawFade = 0;
                    await WaitSkippableAsync(done);
                    break;
                }

                case "A_CHR":
                    AnimateCharacter(line);
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
                        ScreenEffect(line.IntArg(0));
                    break;

                case "MUSIC":
                    if (line.Arg(0).Length == 0)
                        m_audio.Stop(AudioEngine.Music);
                    else if (await Task.Run(() => m_data.ReadAudio(line.Arg(0))) is { } music)
                        m_audio.Play(AudioEngine.Music, music, 0);
                    break;

                case "MUSIC_FADE":
                    m_audio.Stop(AudioEngine.Music, line.IntArg(0, 1000));
                    break;

                case "SE":
                {
                    // $SE,file,plays (0 = loop),channel - no file stops the channel
                    string channel = AudioEngine.Effect(line.IntArg(2));
                    if (line.Arg(0).Length == 0)
                        m_audio.Stop(channel);
                    else if (!(Skipping && line.IntArg(1) != 0) && await Task.Run(() => m_data.ReadAudio(line.Arg(0))) is { } se)
                        m_audio.Play(channel, se, line.IntArg(1));
                    break;
                }

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
    /// $L_CHR,plane,file,x,y,?[,m,base,layer1,...] and $L_MONT,plane,file,x,y,?,m|M,...:
    /// "m" lists the slots (value v at position k = slot k*100+v, -1 = off), "M" gives an
    /// expression code from MONTBL.BIN (the file may then be left out).
    /// </summary>
    private async Task LoadPlaneAsync(int plane, ScriptLine line, int first)
    {
        string file = line.Arg(first);
        double x = line.IntArg(first + 1), y = line.IntArg(first + 2);
        int marker = Array.FindIndex(line.Args, first + 1, a => a is "m" or "M");
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
            m_stage.SetPlane(plane, null);
        else if (expression)
            m_stage.ChangePicture(plane, image, x, y);
        else
            m_stage.SetPlane(plane, image, x, y);
    }

    private async Task<StageImage?> LoadImageAsync(string file) =>
        file.Length == 0 ? null : await Task.Run(() => m_data.LoadImage(file)).WaitAsync(m_token);

    /// <summary>$A_CHR,code,plane,...: plane animations, started by the next $DRAW.</summary>
    private void AnimateCharacter(ScriptLine line)
    {
        int code = line.IntArg(0), plane = line.IntArg(1);
        int P(int i) => line.IntArg(2 + i);
        switch (code)
        {
            case 0:     // stop the plane's animations
                m_stage.ResetPlane(plane);
                break;
            case 1:     // bounce: 0, height, period (guess)
            case 6:     // sway: x, y, period (guess)
                m_stage.QueueSway(plane, P(0), P(1), P(2));
                break;
            case 40:    // screen area the plane is drawn into
                m_stage.SetViewTarget(plane, new Rect(P(0), P(1), P(2), P(3)));
                break;
            case 41:    // part of the plane shown in that area (zoom / pan start)
                m_stage.SetViewSource(plane, new Rect(P(0), P(1), P(2), P(3)));
                break;
            case 42:    // pan / zoom to another part: x, y, w, h, ms, wait
                m_stage.QueueView(plane, new Rect(P(0), P(1), P(2), P(3)), P(4), P(5) != 0);
                break;
            case 62:    // fade in through a rule mask: rule, ms (shown as a plain fade)
                m_stage.QueueFade(plane, true, line.IntArg(3), false);
                break;
            case 114:   // move to x, y: ms, wait
            case 128:
                m_stage.QueueMove(plane, P(0), P(1), P(2), P(3) != 0);
                break;
            case 150:   // fade out: ms, wait
                m_stage.QueueFade(plane, false, P(0), P(1) != 0);
                break;
            case 151:   // fade in: ms, wait
                m_stage.QueueFade(plane, true, P(0), P(1) != 0);
                break;
            case 152:   // cross-fade the plane's new picture: ms
                m_drawFade = Math.Max(m_drawFade, P(0));
                break;
            // 90 / 91: switched on and off around expression changes (lip sync?) - not needed
        }
    }

    /// <summary>$EFECT,n: screen effects from EFCLIB.SCN (not decoded; these are guesses).</summary>
    private void ScreenEffect(int effect)
    {
        switch (effect)
        {
            case 0: m_stage.Shake(0, 6, 300); break;
            case 1: m_stage.Shake(14, 0, 700); break;
            case 2: m_stage.Shake(0, 14, 500); break;
            case 12: m_stage.Flash(400); break;
            default: m_stage.Shake(6, 6, 400); break;
        }
    }

    /// <summary>$EX,group,...: background scroll (9), variables (10) and key waits (2).</summary>
    private async Task ExtendedAsync(ScriptLine line)
    {
        switch (line.IntArg(0))
        {
            case 9:
                switch (line.IntArg(1))
                {
                    case 0: m_stage.ScrollInit(line.IntArg(2), line.IntArg(3)); break;
                    case 1:
                        if (await LoadImageAsync(line.Arg(3)) is { } picture)
                            m_stage.ScrollImage(picture);
                        break;
                    case 2: m_stage.ScrollStart(line.IntArg(2)); break;
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
        string text = ScenarioScript.DisplayText(line.Text!);
        string? voice = m_pendingVoice;
        m_pendingVoice = null;
        AddLog(line.Speaker ?? "", text, voice);

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
