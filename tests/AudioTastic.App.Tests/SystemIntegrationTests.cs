using AudioTastic.App.Integration;
using AudioTastic.Data.Settings;
using AudioTastic.Platform;
using AudioTastic.Platform.Windows;
using Microsoft.Win32;

namespace AudioTastic.App.Tests;

/// <summary>SI-05 context menu entries, SI-06 single instance and the SI-08 switches used by them.</summary>
public sealed class SystemIntegrationTests
{
    [Fact]
    public void Enqueue_and_open_switches_are_parsed_and_files_are_made_absolute()
    {
        var options = StartupOptions.Parse(["--enqueue", "a.flac", "b.mp3"]);
        Assert.Equal(FileAction.Enqueue, options.Action);
        Assert.Equal([Path.GetFullPath("a.flac"), Path.GetFullPath("b.mp3")], options.Files);

        Assert.Equal(FileAction.Open, StartupOptions.Parse(["--open", "a.flac"]).Action);
        var plain = StartupOptions.Parse(["--crash-test", "a.flac"]);
        Assert.Equal(FileAction.Default, plain.Action);
        Assert.True(plain.CrashTest);
        Assert.Single(plain.Files);
    }

    [Fact]
    public void Player_commands_are_parsed_in_a_fixed_order()
    {
        var options = StartupOptions.Parse(["--quit", "--volume", "35", "--next", "--seek", "12.5", "--playpause"]);
        Assert.Equal(["playPause", "next", "volume", "seek", "quit"], options.Controls.Select(c => c.Name));
        Assert.Equal(35, options.Controls.Single(c => c.Name == "volume").Value);
        Assert.Equal(12.5, options.Controls.Single(c => c.Name == "seek").Value);
        Assert.Empty(options.Files);
        Assert.False(options.OnlyQuit);
        Assert.True(StartupOptions.Parse(["--quit"]).OnlyQuit);
        Assert.True(StartupOptions.Parse(["--stop"]).OnlyQuit); // nothing to stop: no player is started
        Assert.True(StartupOptions.Parse(["--pause"]).OnlyQuit);
        Assert.False(StartupOptions.Parse(["--play"]).OnlyQuit);
        Assert.False(StartupOptions.Parse(["--next"]).OnlyQuit);
        Assert.Equal("previous", Assert.Single(StartupOptions.Parse(["--prev"]).Controls).Name);
        Assert.Equal("previous", Assert.Single(StartupOptions.Parse(["--previous"]).Controls).Name);
    }

    [Theory]
    [InlineData("--volume", "101")]
    [InlineData("--volume", "loud")]
    [InlineData("--seek", "-3")]
    [InlineData("--no-such-switch", "a.flac")]
    public void Mistakes_are_reported_instead_of_starting(string option, string value)
    {
        var parsed = CommandLine.Parse([option, value]);
        Assert.Null(parsed.Options);
        Assert.NotEmpty(parsed.Result.Errors);
    }

    [Fact]
    public void Help_and_version_are_answered_without_starting()
    {
        foreach (var help in new[] { "--help", "-h", "-?" })
        {
            var parsed = CommandLine.Parse([help]);
            Assert.Null(parsed.Options);
            Assert.NotNull(parsed.Result.Action);
        }
        Assert.Null(CommandLine.Parse(["--version"]).Options);
        Assert.Empty(CommandLine.Parse(["--help"]).Result.Errors);
    }

    [Fact]
    public void Portable_and_names_starting_with_at_are_not_special()
    {
        var options = StartupOptions.Parse(["--portable", "@home.flac"]);
        Assert.Equal([Path.GetFullPath("@home.flac")], options.Files); // no response files
        Assert.Empty(options.Controls);
    }

    [Fact]
    public void Slash_switches_work_on_windows_only()
    {
        var options = StartupOptions.Parse(["/enqueue", "a.flac"]);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(FileAction.Enqueue, options.Action);
            Assert.Single(options.Files);
            Assert.Equal("next", Assert.Single(StartupOptions.Parse(["/NEXT"]).Controls).Name);
            Assert.Equal(20, Assert.Single(StartupOptions.Parse(["/volume", "20"]).Controls).Value);
        }
        else
        {
            Assert.Equal(FileAction.Default, options.Action); // an absolute path on macOS/Linux
            Assert.Equal(2, options.Files.Count);
        }
    }

    [Fact]
    public void Commands_round_trip_as_json()
    {
        var command = new InstanceCommand(FileAction.Enqueue, [@"C:\Music\ÆØÅ ♫.flac", "/home/a b/c.mp3"], [new("volume", 40), new("next")]);
        var back = InstanceCommand.Deserialize(command.Serialize());
        Assert.NotNull(back);
        Assert.Equal(command.Action, back.Action);
        Assert.Equal(command.Files, back.Files);
        Assert.Equal(command.Controls, back.Controls);
        Assert.Empty(InstanceCommand.Deserialize("""{"Action":"Default","Files":[]}""")!.Controls); // from an older sender
        Assert.Null(InstanceCommand.Deserialize("not json"));
        Assert.Null(InstanceCommand.Deserialize("{}"));
    }

    [Fact]
    public void Batch_sorts_files_from_several_starts_and_keeps_one_starts_order()
    {
        // Explorer starts one process per selected file, in no reliable order.
        var batch = InstanceBatch.Merge(
        [
            new(FileAction.Enqueue, [@"C:\m\03.flac"]),
            new(FileAction.Enqueue, [@"C:\m\01.flac"]),
            new(FileAction.Enqueue, [@"C:\m\02.flac"]),
        ], OpenFilesAction.Play);
        Assert.Equal([@"C:\m\01.flac", @"C:\m\02.flac", @"C:\m\03.flac"], batch.Enqueue);
        Assert.Empty(batch.Play);
        Assert.False(batch.Activate); // enqueueing leaves the player where it is

        var single = InstanceBatch.Merge([new(FileAction.Open, ["b.flac", "a.flac"])], OpenFilesAction.Play);
        Assert.Equal(["b.flac", "a.flac"], single.Play);
        Assert.True(single.Activate);
    }

    [Fact]
    public void Plain_files_follow_the_setting_and_no_files_brings_the_player_forward()
    {
        InstanceCommand plain = new(FileAction.Default, ["a.flac"]);
        Assert.Equal(["a.flac"], InstanceBatch.Merge([plain], OpenFilesAction.Play).Play);
        Assert.Equal(["a.flac"], InstanceBatch.Merge([plain], OpenFilesAction.Enqueue).Enqueue);
        Assert.Equal(["a.flac"], InstanceBatch.Merge([new(FileAction.Open, ["a.flac"])], OpenFilesAction.Enqueue).Play);

        var control = InstanceBatch.Merge([new(FileAction.Default, [], [new("next")]), new(FileAction.Default, [], [new("pause")])], OpenFilesAction.Play);
        Assert.False(control.Activate); // a script's /next must not pull the player in front
        Assert.Equal(["next", "pause"], control.Controls.Select(c => c.Name));

        var activate = InstanceBatch.Merge([new(FileAction.Default, [])], OpenFilesAction.Play);
        Assert.True(activate.Activate);
        Assert.Empty(activate.Play);
        Assert.Empty(activate.Enqueue);
    }

    [Fact]
    public void Inbox_keeps_commands_until_the_player_is_ready()
    {
        var inbox = new InstanceInbox();
        inbox.Post(new(FileAction.Open, ["a.flac"]));
        var handled = new List<InstanceCommand>();
        var early = inbox.Attach(handled.Add);
        Assert.Single(early);
        inbox.Post(new(FileAction.Enqueue, ["b.flac"]));
        Assert.Equal(["b.flac"], Assert.Single(handled).Files);
    }

    [Fact]
    public async Task Second_start_hands_its_files_to_the_running_instance()
    {
        var root = Directory.CreateTempSubdirectory("audiotastic-si-").FullName;
        try
        {
            var received = new TaskCompletionSource<InstanceCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var first = SingleInstance.TryAcquire(root))
            {
                Assert.NotNull(first);
                Assert.Null(await Task.Run(() => SingleInstance.TryAcquire(root), TestContext.Current.CancellationToken)); // owned elsewhere
                first.Listen(c => received.TrySetResult(c));

                var sent = await Task.Run(() => SingleInstance.TrySend(root, new(FileAction.Enqueue, ["x.flac"]), TimeSpan.FromSeconds(5)),
                    TestContext.Current.CancellationToken);
                Assert.True(sent);
                var command = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(FileAction.Enqueue, command.Action);
                Assert.Equal(["x.flac"], command.Files);
            }
            // The first one has ended: the next start becomes the running instance, and nobody answers sends.
            using var next = await Task.Run(() => SingleInstance.TryAcquire(root), TestContext.Current.CancellationToken);
            Assert.NotNull(next);
            Assert.False(SingleInstance.TrySend(root, new(FileAction.Default, []), TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Other_data_folders_and_users_get_other_names()
    {
        var a = SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "one"));
        Assert.Equal(a, SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "one") + Path.DirectorySeparatorChar));
        Assert.NotEqual(a, SingleInstance.NameFor(Path.Combine(Path.GetTempPath(), "two")));
    }

    [Fact]
    public void Context_menu_default_is_on_except_in_portable_mode()
    {
        var settings = new SystemIntegrationSettings();
        Assert.True(ShellIntegrationService.IsEnabled(settings, portable: false));
        Assert.False(ShellIntegrationService.IsEnabled(settings, portable: true));
        settings.ContextMenu = true;
        Assert.True(ShellIntegrationService.IsEnabled(settings, portable: true));
        settings.ContextMenu = false;
        Assert.False(ShellIntegrationService.IsEnabled(settings, portable: false));
    }

    [Fact]
    public void Windows_verbs_are_written_updated_and_removed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows registry");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var testRoot = @"Software\AudioTasticTests\" + Guid.NewGuid().ToString("N");
        var classes = testRoot + @"\Classes";
        var shell = new WindowsShellIntegration(Registry.CurrentUser, testRoot);
        const string exe = @"C:\Programs\AudioTastic\AudioTastic.exe";
        try
        {
            // Another program's verb on .mp3 must survive.
            using (var other = Registry.CurrentUser.CreateSubKey(classes + @"\SystemFileAssociations\.mp3\shell\Other\command"))
            {
                other.SetValue("", "other.exe \"%1\"");
            }

            shell.SetContextMenu(new ContextMenuEntries(exe, [".FLAC", ".mp3", "bad ext", ".m3u8"], "Open in AudioTastic", "Enqueue in AudioTastic"));
            Assert.Equal([".flac", ".m3u8", ".mp3"], shell.RegisteredExtensions());
            using (var play = Registry.CurrentUser.OpenSubKey($@"{classes}\SystemFileAssociations\.flac\shell\{WindowsShellIntegration.OpenVerb}"))
            {
                Assert.NotNull(play);
                Assert.Equal("Open in AudioTastic", play.GetValue("MUIVerb"));
                Assert.Equal($"\"{exe}\",0", play.GetValue("Icon"));
                Assert.Equal($"\"{exe}\" --open \"%1\"", play.OpenSubKey("command")!.GetValue(""));
            }
            using (var queue = Registry.CurrentUser.OpenSubKey($@"{classes}\SystemFileAssociations\.flac\shell\{WindowsShellIntegration.EnqueueVerb}\command"))
            {
                Assert.Equal($"\"{exe}\" --enqueue \"%1\"", queue!.GetValue(""));
            }

            // A file type no longer supported loses its entries; the others stay.
            shell.SetContextMenu(new ContextMenuEntries(exe, [".mp3"], "Open in AudioTastic", "Enqueue in AudioTastic"));
            Assert.Equal([".mp3"], shell.RegisteredExtensions());
            Assert.Null(Registry.CurrentUser.OpenSubKey($@"{classes}\SystemFileAssociations\.flac"));

            shell.SetContextMenu(null);
            Assert.Empty(shell.RegisteredExtensions());
            using var mp3 = Registry.CurrentUser.OpenSubKey($@"{classes}\SystemFileAssociations\.mp3\shell");
            Assert.NotNull(mp3);
            Assert.Equal(["Other"], mp3.GetSubKeyNames());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\AudioTasticTests", writable: true);
            if (parent is { SubKeyCount: 0 })
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\AudioTasticTests", throwOnMissingSubKey: false);
            }
        }
    }

    [Fact]
    public void Windows_file_types_are_registered_without_taking_over_defaults_and_removed_cleanly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows registry");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var testRoot = @"Software\AudioTasticTests\" + Guid.NewGuid().ToString("N");
        var classes = testRoot + @"\Classes";
        var shell = new WindowsShellIntegration(Registry.CurrentUser, testRoot);
        const string exe = @"C:\Programs\AudioTastic\AudioTastic.exe";
        try
        {
            // Another player's registration and the user's own default must survive.
            using (var other = Registry.CurrentUser.CreateSubKey(classes + @"\.mp3\OpenWithProgids"))
            {
                other.SetValue("OtherPlayer.mp3", Array.Empty<byte>(), RegistryValueKind.None);
            }
            using (var mp3 = Registry.CurrentUser.CreateSubKey(classes + @"\.mp3"))
            {
                mp3.SetValue("", "OtherPlayer.mp3");
            }

            Assert.True(shell.SetFileAssociations(new FileAssociations(exe, [".FLAC", ".mp3", ".m3u8"], [".m3u8", ".cue"])));
            Assert.False(shell.SetFileAssociations(new FileAssociations(exe, [".flac", ".mp3", ".m3u8"], [".m3u8", ".cue"]))); // unchanged
            Assert.Equal([".flac", ".mp3", ".cue", ".m3u8"], shell.AssociatedExtensions());

            using (var open = Registry.CurrentUser.OpenSubKey($@"{classes}\{WindowsShellIntegration.AudioProgId}\shell\open\command"))
            {
                Assert.Equal($"\"{exe}\" \"%1\"", open!.GetValue(""));
            }
            using (var flac = Registry.CurrentUser.OpenSubKey(classes + @"\.flac\OpenWithProgids"))
            {
                Assert.Contains(WindowsShellIntegration.AudioProgId, flac!.GetValueNames());
            }
            using (var m3u8 = Registry.CurrentUser.OpenSubKey(classes + @"\.m3u8\OpenWithProgids"))
            {
                Assert.Equal([WindowsShellIntegration.PlaylistProgId], m3u8!.GetValueNames()); // a playlist, not audio
            }
            using (var mp3 = Registry.CurrentUser.OpenSubKey(classes + @"\.mp3"))
            {
                Assert.Equal("OtherPlayer.mp3", mp3!.GetValue("")); // never takes over the default
            }
            using (var capabilities = Registry.CurrentUser.OpenSubKey(testRoot + @"\AudioTastic\Capabilities\FileAssociations"))
            {
                Assert.Equal(WindowsShellIntegration.PlaylistProgId, capabilities!.GetValue(".cue"));
                Assert.Equal(WindowsShellIntegration.AudioProgId, capabilities.GetValue(".flac"));
            }
            using (var registered = Registry.CurrentUser.OpenSubKey(testRoot + @"\RegisteredApplications"))
            {
                Assert.Equal(testRoot + @"\AudioTastic\Capabilities", registered!.GetValue("AudioTastic"));
            }

            // A type no longer played loses its registration.
            Assert.True(shell.SetFileAssociations(new FileAssociations(exe, [".mp3"], [])));
            Assert.Null(Registry.CurrentUser.OpenSubKey(classes + @"\.flac"));

            Assert.True(shell.SetFileAssociations(null));
            Assert.Empty(shell.AssociatedExtensions());
            Assert.Null(Registry.CurrentUser.OpenSubKey($@"{classes}\{WindowsShellIntegration.AudioProgId}"));
            Assert.Null(Registry.CurrentUser.OpenSubKey(testRoot + @"\AudioTastic\Capabilities"));
            using (var mp3 = Registry.CurrentUser.OpenSubKey(classes + @"\.mp3\OpenWithProgids"))
            {
                Assert.Equal(["OtherPlayer.mp3"], mp3!.GetValueNames());
            }
            Assert.False(shell.SetFileAssociations(null));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\AudioTasticTests", writable: true);
            if (parent is { SubKeyCount: 0 })
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\AudioTasticTests", throwOnMissingSubKey: false);
            }
        }
    }

    [Fact]
    public void Tray_tooltip_shows_the_title_within_the_windows_limit()
    {
        Assert.Equal("AudioTastic", AudioTastic.App.Ui.TrayController.ToolTipFor(null));
        Assert.Equal("AudioTastic\nArtist - Title",AudioTastic.App.Ui.TrayController.ToolTipFor("  Artist - Title "));
        var longTitle = AudioTastic.App.Ui.TrayController.ToolTipFor(new string('x', 300));
        Assert.Equal(AudioTastic.App.Ui.TrayController.MaxToolTipLength, longTitle.Length);
        Assert.EndsWith("…", longTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void Media_controls_show_buffering_as_playing()
    {
        Assert.Equal(MediaPlaybackState.Playing, MediaControlsService.StateOf(AudioTastic.Core.Playback.PlaybackState.Playing));
        Assert.Equal(MediaPlaybackState.Playing, MediaControlsService.StateOf(AudioTastic.Core.Playback.PlaybackState.Buffering));
        Assert.Equal(MediaPlaybackState.Paused, MediaControlsService.StateOf(AudioTastic.Core.Playback.PlaybackState.Paused));
        Assert.Equal(MediaPlaybackState.Stopped, MediaControlsService.StateOf(AudioTastic.Core.Playback.PlaybackState.Stopped));
    }

    [Fact]
    public void Start_menu_shortcut_carries_the_app_identity_and_is_rewritten_when_the_program_moves()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows shell links");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var folder = Directory.CreateTempSubdirectory("audiotastic-lnk-").FullName;
        var testRoot = @"Software\AudioTasticTests\" + Guid.NewGuid().ToString("N");
        try
        {
            var shell = new WindowsShellIntegration(Registry.CurrentUser, testRoot, programsFolder: folder);
            var exe = Environment.ProcessPath!; // any existing program will do
            shell.SetStartMenuShortcut(exe, "BigApps.AudioTastic.Test");
            var (target, appId) = StartMenuShortcut.Read(shell.ShortcutPath);
            Assert.Equal(exe, target, ignoreCase: true);
            Assert.Equal("BigApps.AudioTastic.Test", appId);

            var written = File.GetLastWriteTimeUtc(shell.ShortcutPath);
            shell.SetStartMenuShortcut(exe, "BigApps.AudioTastic.Test"); // unchanged: not rewritten
            Assert.Equal(written, File.GetLastWriteTimeUtc(shell.ShortcutPath));

            var other = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            shell.SetStartMenuShortcut(other, "BigApps.AudioTastic.Test");
            Assert.Equal(other, StartMenuShortcut.Read(shell.ShortcutPath).Target, ignoreCase: true);

            shell.SetStartMenuShortcut(null, "BigApps.AudioTastic.Test");
            Assert.False(File.Exists(shell.ShortcutPath));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
            Registry.CurrentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false);
        }
    }
}
