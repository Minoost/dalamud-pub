using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.DiscordBot;
using Dalamud.Game;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Actors.Types;
using Dalamud.Game.ClientState.Actors.Types.NonPlayer;
using Dalamud.Game.Command;
using Dalamud.Game.Internal;
using Dalamud.Game.Internal.Gui;
using Dalamud.Game.Network;
using Dalamud.Plugin;
using Serilog;
using XIVLauncher.Dalamud;

namespace Dalamud {
    public sealed class Dalamud : IDisposable {
        private readonly string baseDirectory;

        private readonly ManualResetEvent unloadSignal;

        private readonly ProcessModule targetModule;
        private readonly SigScanner sigScanner;

        public Framework Framework { get; }

        public CommandManager CommandManager { get; }
        public ChatHandlers ChatHandlers { get; }
        public NetworkHandlers NetworkHandlers { get; }

        public readonly DiscordBotManager BotManager;

        public readonly PluginManager PluginManager;

        public readonly ClientState ClientState;

        public readonly DalamudStartInfo StartInfo;

        public readonly IconReplacer IconReplacer;

        public readonly DalamudConfiguration Configuration;

        public Dalamud(DalamudStartInfo info) {
            this.StartInfo = info;
            this.Configuration = DalamudConfiguration.Load(info.ConfigurationPath);
            
            this.baseDirectory = info.WorkingDirectory;

            this.unloadSignal = new ManualResetEvent(false);

            // Initialize the process information.
            this.targetModule = Process.GetCurrentProcess().MainModule;
            this.sigScanner = new SigScanner(this.targetModule);

            // Initialize game subsystem
            Framework = new Framework(this.sigScanner, this);

            // Initialize managers. Basically handlers for the logic
            CommandManager = new CommandManager(this, info.Language);
            SetupCommands();

            ChatHandlers = new ChatHandlers(this);
            NetworkHandlers = new NetworkHandlers(this, this.Configuration.OptOutMbCollection);

            this.ClientState = new ClientState(this, info, this.sigScanner, this.targetModule);

            this.BotManager = new DiscordBotManager(this, this.Configuration.DiscordFeatureConfig);

            this.PluginManager = new PluginManager(this, info.PluginDirectory, info.DefaultPluginDirectory);

            this.IconReplacer = new IconReplacer(this, this.sigScanner);
            
            try {
                this.PluginManager.LoadPlugins();
            } catch (Exception ex) {
                Framework.Gui.Chat.PrintError(
                    "[XIVLAUNCHER] There was an error loading additional plugins. Please check the log for more details.");
                Log.Error(ex, "Plugin load failed.");
            }
        }

        public void Start() {
            Framework.Enable();

            this.BotManager.Start();

            if (this.Configuration.ComboPresets != CustomComboPreset.None)
                this.IconReplacer.Enable();
        }

        public void Unload() {
            this.unloadSignal.Set();
        }

        public void WaitForUnload() {
            this.unloadSignal.WaitOne();
        }

        public void Dispose() {
            Framework.Dispose();

            this.BotManager.Dispose();

            this.unloadSignal.Dispose();

            if (this.Configuration.ComboPresets != CustomComboPreset.None)
                this.IconReplacer.Dispose();
        }

        private void SetupCommands() {
            CommandManager.AddHandler("/xldclose", new CommandInfo(OnUnloadCommand) {
                HelpMessage = "Unloads XIVLauncher in-game addon.",
                ShowInHelp = false
            });

            CommandManager.AddHandler("/xldreloadplugins", new CommandInfo(OnPluginReloadCommand) {
                HelpMessage = "Reloads all plugins.",
                ShowInHelp = false
            });

            CommandManager.AddHandler("/xldsay", new CommandInfo(OnCommandDebugSay) {
                HelpMessage = "Print to chat.",
                ShowInHelp = false
            });

            CommandManager.AddHandler("/xldcombo", new CommandInfo(OnCommandDebugCombo) {
                HelpMessage = "COMBO debug",
                ShowInHelp = false
            });

            CommandManager.AddHandler("/xlhelp", new CommandInfo(OnCommandHelp) {
                HelpMessage = "Shows list of commands available."
            });

            CommandManager.AddHandler("/fatewatchadd", new CommandInfo(OnFateWatchAdd) {
                HelpMessage = "Add a fate to your watch list by name. Usage: /fatewatchadd <name of fate>"
            });

            CommandManager.AddHandler("/fatewatchlist", new CommandInfo(OnFateWatchList) {
                HelpMessage = "List fates you're currently watching."
            });

            CommandManager.AddHandler("/fatewatchremove", new CommandInfo(OnFateWatchRemove) {
                HelpMessage = "Remove a fate from your watch list. Usage: /fatewatchremove <name of fate>"
            });

            CommandManager.AddHandler("/xlmute", new CommandInfo(OnBadWordsAdd) {
                HelpMessage = "Mute a word or sentence from appearing in chat. Usage: /xlmute <word or sentence>"
            });

            CommandManager.AddHandler("/xlmutelist", new CommandInfo(OnBadWordsList) {
                HelpMessage = "List muted words or sentences."
            });

            CommandManager.AddHandler("/xlunmute", new CommandInfo(OnBadWordsRemove) {
                HelpMessage = "Unmute a word or sentence. Usage: /fatewatchremove <word or sentence>"
            });

            CommandManager.AddHandler("/xldstate", new CommandInfo(OnDebugPrintGameState) {
                HelpMessage = "Print parsed game state",
                ShowInHelp = false
            });

            CommandManager.AddHandler("/ll", new CommandInfo(OnLastLinkCommand) {
                HelpMessage = "Open the last posted link in your default browser."
            });

            CommandManager.AddHandler("/xlbotjoin", new CommandInfo(OnBotJoinCommand) {
                HelpMessage = "Add the XIVLauncher discord bot you set up to your server."
            });

            CommandManager.AddHandler("/xlbgmset", new CommandInfo(OnBgmSetCommand)
            {
                HelpMessage = "Set the Game background music. Usage: /bgmset <BGM ID>"
            });

            CommandManager.AddHandler("/xlitem", new CommandInfo(OnItemLinkCommand)
            {
                HelpMessage = "Link an item by name. Usage: /item <Item name>"
            });
        }

        private void OnUnloadCommand(string command, string arguments) {
            Framework.Gui.Chat.Print("Unloading...");
            Unload();
        }

        private void OnCommandHelp(string command, string arguments) {
            var showDebug = arguments.Contains("debug");

            Framework.Gui.Chat.Print("Available commands:");
            foreach (var cmd in CommandManager.Commands) {
                if (!cmd.Value.ShowInHelp && !showDebug)
                    continue;

                Framework.Gui.Chat.Print($"{cmd.Key}: {cmd.Value.HelpMessage}");
            }
        }

        private void OnCommandDebugSay(string command, string arguments) {
            var parts = arguments.Split();

            var chatType = (XivChatType) int.Parse(parts[0]);
            var msg = string.Join(" ", parts.Take(1).ToArray());

            Framework.Gui.Chat.PrintChat(new XivChatEntry {
                MessageBytes = Encoding.UTF8.GetBytes(msg),
                Name = "Xiv Launcher",
                Type = chatType
            });
        }

        private void OnPluginReloadCommand(string command, string arguments) {
            Framework.Gui.Chat.Print("Reloading...");

            try {
                this.PluginManager.UnloadPlugins();
                this.PluginManager.LoadPlugins();

                Framework.Gui.Chat.Print("OK");
            } catch (Exception ex) {
                Framework.Gui.Chat.PrintError("Reload failed.");
                Log.Error(ex, "Plugin reload failed.");
            }
        }

        private void OnFateWatchAdd(string command, string arguments) {
            if (this.Configuration.Fates == null)
                this.Configuration.Fates = new List<DalamudConfiguration.FateInfo>();

            dynamic candidates = XivApi.Search(arguments, "Fate").GetAwaiter().GetResult();

            if (candidates.Results.Count == 0) {
                Framework.Gui.Chat.Print("No fates found using that name.");
                return;
            }

            var fateInfo = new DalamudConfiguration.FateInfo {
                Id = candidates.Results[0].ID,
                Name = candidates.Results[0].Name
            };

            this.Configuration.Fates.Add(fateInfo);

            this.Configuration.Save(this.StartInfo.ConfigurationPath);

            Framework.Gui.Chat.Print($"Added fate \"{fateInfo.Name}\".");
        }

        private void OnFateWatchList(string command, string arguments) {
            if (this.Configuration.Fates == null)
                this.Configuration.Fates = new List<DalamudConfiguration.FateInfo>();

            if (this.Configuration.Fates.Count == 0) {
                Framework.Gui.Chat.Print("No fates on your watchlist.");
                return;
            }

            this.Configuration.Save(this.StartInfo.ConfigurationPath);

            foreach (var fate in this.Configuration.Fates)
                Framework.Gui.Chat.Print($"Fate {fate.Id}: {fate.Name}");
        }

        private void OnFateWatchRemove(string command, string arguments) {
            if (this.Configuration.Fates == null)
                this.Configuration.Fates = new List<DalamudConfiguration.FateInfo>();

            dynamic candidates = XivApi.Search(arguments, "Fate").GetAwaiter().GetResult();

            if (candidates.Results.Count == 0) {
                Framework.Gui.Chat.Print("No fates found using that name.");
                return;
            }

            this.Configuration.Fates.RemoveAll(x => x.Id == candidates.Results[0].ID);

            this.Configuration.Save(this.StartInfo.ConfigurationPath);

            Framework.Gui.Chat.Print($"Removed fate \"{candidates.Results[0].Name}\".");
        }

        private void OnBadWordsAdd(string command, string arguments) {
            if (this.Configuration.BadWords == null)
                this.Configuration.BadWords = new List<string>();

            this.Configuration.BadWords.Add(arguments);

            this.Configuration.Save(this.StartInfo.ConfigurationPath);

            Framework.Gui.Chat.Print($"Muted \"{arguments}\".");
        }

        private void OnBadWordsList(string command, string arguments) {
            if (this.Configuration.BadWords == null)
                this.Configuration.BadWords = new List<string>();

            if (this.Configuration.BadWords.Count == 0) {
                Framework.Gui.Chat.Print("No muted words or sentences.");
                return;
            }

            this.Configuration.Save(this.StartInfo.ConfigurationPath);

            foreach (var word in this.Configuration.BadWords) Framework.Gui.Chat.Print($"\"{word}\"");
        }

        private void OnBadWordsRemove(string command, string arguments) {
            if (this.Configuration.BadWords == null)
                this.Configuration.BadWords = new List<string>();

            this.Configuration.BadWords.RemoveAll(x => x == arguments);

            this.Configuration.Save(this.StartInfo.ConfigurationPath);

            Framework.Gui.Chat.Print($"Unmuted \"{arguments}\".");
        }

        private void OnLastLinkCommand(string command, string arguments) {
            if (string.IsNullOrEmpty(ChatHandlers.LastLink)) {
                Framework.Gui.Chat.Print("No last link...");
                return;
            }

            Framework.Gui.Chat.Print("Opening " + ChatHandlers.LastLink);
            Process.Start(ChatHandlers.LastLink);
        }

        private void OnDebugPrintGameState(string command, string arguments) {
            Framework.Gui.Chat.Print(this.ClientState.Actors.Length + " entries");
            Framework.Gui.Chat.Print(this.ClientState.LocalPlayer.Name);
            Framework.Gui.Chat.Print(this.ClientState.LocalPlayer.CurrentWorld.Name);
            Framework.Gui.Chat.Print(this.ClientState.LocalPlayer.HomeWorld.Name);
            Framework.Gui.Chat.Print(this.ClientState.LocalContentId.ToString("X"));
            Framework.Gui.Chat.Print(Framework.Gui.Chat.LastLinkedItemId.ToString());

            for (var i = 0; i < this.ClientState.Actors.Length; i++) {
                var actor = this.ClientState.Actors[i];

                Log.Debug(actor.Name);
                Framework.Gui.Chat.Print(
                    $"{i} - {actor.Name} - {actor.Position.X} {actor.Position.Y} {actor.Position.Z}");

                if (actor is Npc npc)
                    Framework.Gui.Chat.Print($"DataId: {npc.DataId}");

                if (actor is Chara chara)
                    Framework.Gui.Chat.Print(
                        $"Level: {chara.Level} ClassJob: {chara.ClassJob.Name} CHP: {chara.CurrentHp} MHP: {chara.MaxHp} CMP: {chara.CurrentMp} MMP: {chara.MaxMp}");
            }
        }

        private void OnCommandDebugCombo(string command, string arguments) {
            var argumentsParts = arguments.Split();

            switch (argumentsParts[0]) {
                case "setall": {
                    foreach (var value in Enum.GetValues(typeof(CustomComboPreset)).Cast<CustomComboPreset>()) {
                        if (value == CustomComboPreset.None)
                            continue;

                        this.Configuration.ComboPresets |= value;
                    }

                    Framework.Gui.Chat.Print("all SET");
                }
                    break;
                case "unsetall": {
                    foreach (var value in Enum.GetValues(typeof(CustomComboPreset)).Cast<CustomComboPreset>()) {
                        this.Configuration.ComboPresets &= value;
                    }

                    Framework.Gui.Chat.Print("all UNSET");
                }
                    break;
                case "set": {
                    foreach (var value in Enum.GetValues(typeof(CustomComboPreset)).Cast<CustomComboPreset>()) {
                        if (value.ToString().ToLower() != argumentsParts[1].ToLower())
                            continue;

                        this.Configuration.ComboPresets |= value;
                    }
                }
                    break;
                case "toggle": {
                    foreach (var value in Enum.GetValues(typeof(CustomComboPreset)).Cast<CustomComboPreset>()) {
                        if (value.ToString().ToLower() != argumentsParts[1].ToLower())
                            continue;

                        this.Configuration.ComboPresets ^= value;
                    }
                }
                    break;

                case "unset": {
                    foreach (var value in Enum.GetValues(typeof(CustomComboPreset)).Cast<CustomComboPreset>()) {
                        if (value.ToString().ToLower() != argumentsParts[1].ToLower())
                            continue;

                        this.Configuration.ComboPresets &= ~value;
                    }
                }
                    break;

                case "list": {
                    foreach (var value in Enum.GetValues(typeof(CustomComboPreset)).Cast<CustomComboPreset>()) {
                        if (this.Configuration.ComboPresets.HasFlag(value))
                            Framework.Gui.Chat.Print(value.ToString());
                    }
                }
                    break;

                default: Framework.Gui.Chat.Print("Unknown");
                    break;
            }

            this.Configuration.Save(this.StartInfo.ConfigurationPath);
        }

        private void OnBotJoinCommand(string command, string arguments) {
            if (this.BotManager != null && this.BotManager.IsConnected)
                Process.Start(
                    $"https://discordapp.com/oauth2/authorize?client_id={this.BotManager.UserId}&scope=bot&permissions=117760");
            else
                Framework.Gui.Chat.Print(
                    "The XIVLauncher discord bot was not set up correctly or could not connect to discord. Please check the settings and the FAQ.");
        }

        private void OnBgmSetCommand(string command, string arguments)
        {
            Framework.Network.InjectBgmTest(int.Parse(arguments));
        }

        private void OnItemLinkCommand(string command, string arguments) {
            Task.Run(async () => {
                try {
                    dynamic results = await XivApi.Search(arguments, "Item", 1);
                    var itemId = (short) results.Results[0].ID;
                    var itemName = (string)results.Results[0].Name;

                    var hexData = new byte[] {
                        0x02, 0x13, 0x06, 0xFE, 0xFF, 0xF3, 0xF3, 0xF3, 0x03, 0x02, 0x27, 0x07, 0x03, 0xF2, 0x3A, 0x2F,
                        0x02, 0x01, 0x03, 0x02, 0x13, 0x06, 0xFE, 0xFF, 0xFF, 0x7B, 0x1A, 0x03, 0xEE, 0x82, 0xBB, 0x02,
                        0x13, 0x02, 0xEC, 0x03
                    };

                    var endTag = new byte[] {
                        0x02, 0x27, 0x07, 0xCF, 0x01, 0x01, 0x01, 0xFF, 0x01, 0x03, 0x02, 0x13, 0x02, 0xEC, 0x03
                    };

                    BitConverter.GetBytes(itemId).Reverse().ToArray().CopyTo(hexData, 14);

                    hexData = hexData.Concat(Encoding.UTF8.GetBytes(itemName)).Concat(endTag).ToArray();

                    Framework.Gui.Chat.PrintChat(new XivChatEntry {
                        MessageBytes = hexData
                    });
                }
                catch {
                    Framework.Gui.Chat.PrintError("Could not find item.");
                }

            });
        }

        public static byte[] StringToByteArray(String value)
        {
            byte[] bytes = new byte[value.Length * sizeof(char)];
            Buffer.BlockCopy(value.ToCharArray(), 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public static String ByteArrayToString(byte[] value)
        {
            var chars = new char[value.Length / sizeof(char)];

            var atValue = 0;
            for (var i = 0; i < chars.Length; i++) {
                chars[i] = BitConverter.ToChar(value, atValue);

                atValue += 2;
            }

            return new string(chars);
        }
    }
}
