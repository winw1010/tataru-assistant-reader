using Sharlayan;
using Sharlayan.Core;
using Sharlayan.Enums;
using Sharlayan.Models;
using Sharlayan.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace tataru_assistant_reader
{
    class Program
    {
        private static bool _keepAlive = true;
        private static bool _keepRunning = false;

        static async Task Main(string[] args)
        {
            // SIGINT
            Console.CancelKeyPress += (sender, e) =>
            {
                _keepAlive = false;
            };

            // SIGTERM
            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                _keepAlive = false;
            };

            try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }

            _ = AliveCheck();

            while (_keepAlive)
            {
                try
                {
                    var memoryHandler = SystemFunction.CreateMemoryHandler();
                    await ReadText(memoryHandler);
                }
                catch (Exception ex)
                {
                    await SystemFunction.WriteSystemMessage(ex.Message);
                }

                await Task.Delay(1000);
            }
        }

        private static async Task ReadText(MemoryHandler memoryHandler)
        {
            await SystemFunction.WriteSystemMessage("Start reading...");

            while (_keepAlive && _keepRunning)
            {
                if (!memoryHandler.Scanner.IsScanning)
                {
                    await Task.WhenAll(
                        ReaderFunction.ReadDialog(memoryHandler),
                        ReaderFunction.ReadChatLog(memoryHandler),
                        ReaderFunction.ReadCutscene(memoryHandler)
                        );
                }

                await Task.Delay(50);
            }

            await SystemFunction.WriteSystemMessage("Stop reading...");
        }

        private static async Task AliveCheck()
        {
            while (_keepAlive)
            {
                try
                {
                    Process[] processes = Process.GetProcessesByName("ffxiv_dx11");

                    if (processes.Length > 0)
                    {
                        _keepRunning = true;
                    }
                    else
                    {
                        _keepRunning = false;
                    }
                }
                catch (Exception)
                {
                }

                await Task.Delay(1000);
            }
        }
    }

    class SystemFunction
    {
        private static string _lastText = "";

        public static MemoryHandler CreateMemoryHandler()
        {
            // Get process
            Process[] processes = Process.GetProcessesByName("ffxiv_dx11");

            if (!(processes.Length > 0)) { throw new Exception("Waiting..."); }

            // supported: Global, Chinese, Korean
            GameRegion gameRegion = GameRegion.Global;
            GameLanguage gameLanguage = GameLanguage.English;

            // whether to always hit API on start to get the latest sigs based on patchVersion, or use the local json cache (if the file doesn't exist, API will be hit)
            bool useLocalCache = true;

            // patchVersion of game, or latest
            string patchVersion = "latest";

            // process of game
            var processModel = new ProcessModel
            {
                Process = processes[0],
            };

            // Create configuration
            var configuration = new SharlayanConfiguration
            {
                ProcessModel = processModel,
                GameLanguage = gameLanguage,
                GameRegion = gameRegion,
                PatchVersion = patchVersion,
                UseLocalCache = useLocalCache
            };

            // Create memoryHandler
            var memoryHandler = SharlayanMemoryManager.Instance.AddHandler(configuration);

            // Set signatures
            string signaturesText = File.ReadAllText("signatures.json");
            var signatures = JsonUtilities.Deserialize<List<Signature>>(signaturesText);
            if (signatures != null)
            {
                memoryHandler.Scanner.LoadOffsets(signatures.ToArray());
            }

            return memoryHandler;
        }

        public static async Task WriteSystemMessage(string text)
        {
            if (text == _lastText) return;
            _lastText = text;
            await WriteData("SYSTEM", "FFFF", "", text);
        }

        public static async Task WriteData(string type, string code, string name, string text, int sleepTime = 0)
        {
            await Task.Delay(sleepTime);

            if (!(text.Length > 0)) { return; }

            string dataString = JsonUtilities.Serialize(new
            {
                type,
                code,
                name,
                text,
            }) + "\r\n";

            Console.Write(dataString);
        }
    }

    class ReaderFunction
    {
        private static int _previousArrayIndex = 0;
        private static int _previousOffset = 0;

        private static string _lastDialogText = "";

        private static List<ChatLogItem> _lastChatLogEntries = new List<ChatLogItem>();

        private static string _lastCutsceneText = "";

        private static readonly List<string> _systemCodes = new List<string>() { "0039", "0839", "0003", "0038", "003C", "0048", "001D", "001C" };

        private static readonly List<string> _knockDownNames = new List<string>() { "Down for the Count", "Au tapis", "Am Boden", "ノックダウン" };
        private static readonly List<short> _knockDownCodes = new List<short>() { 625, 774, 783, 896, 1762, 1785, 1950, 1953, 1963, 2408, 2910, 2961 };

        private static readonly List<string> _preoccupiedNames = new List<string>() { "Preoccupied", "En action", "Handelt", "行動中" };
        private static readonly List<short> _preoccupiedCodes = new List<short>() { 1619 };

        public static async Task ReadDialog(MemoryHandler memoryHandler)
        {
            try
            {
                byte[] rawDialogName = GetRealBytes(memoryHandler.GetByteArray(memoryHandler.Scanner.Locations["PANEL_NAME"], 128));
                byte[] rawDialogText = GetRealBytes(memoryHandler.GetByteArray(memoryHandler.Scanner.Locations["PANEL_TEXT"], 512));

                string dialogName = XMLCleaner.SanitizeXmlString(ChatEntry.ProcessFullLine("003D", rawDialogName)).Trim();
                string dialogText = XMLCleaner.SanitizeXmlString(ChatEntry.ProcessFullLine("003D", rawDialogText)).Trim();

                if (dialogName.Length > 0 && dialogText.Length > 0 && dialogText != _lastDialogText)
                {
                    _lastDialogText = dialogText;
                    await SystemFunction.WriteData("DIALOG", "003D", dialogName, dialogText);
                }
            }
            catch (Exception)
            {
            }
        }

        public static async Task ReadChatLog(MemoryHandler memoryHandler)
        {
            try
            {
                var readResult = memoryHandler.Reader.GetChatLog(_previousArrayIndex, _previousOffset);
                List<ChatLogItem> chatLogEntries = readResult.ChatLogItems.ToList();

                _previousArrayIndex = readResult.PreviousArrayIndex;
                _previousOffset = readResult.PreviousOffset;

                if (chatLogEntries.Count > 0)
                {
                    if (ArrayFunction.IsSameChatLogEntries(chatLogEntries, _lastChatLogEntries))
                    {
                        return;
                    }
                    else
                    {
                        _lastChatLogEntries = chatLogEntries;
                    }

                    for (int i = 0; i < chatLogEntries.Count; i++)
                    {
                        var chatLogItem = chatLogEntries[i];

                        string logName = chatLogItem.PlayerName != null ? chatLogItem.PlayerName : "";
                        string logText = chatLogItem.Message;

                        if (logName.Length == 0 && !_systemCodes.Contains(chatLogItem.Code))
                        {
                            string[] splitedMessage = logText.Split(':');
                            if (splitedMessage[0].Length > 0 && splitedMessage.Length > 1)
                            {
                                logName = splitedMessage[0];
                                logText = logText.Replace(logName + ":", "");
                            }
                        }

                        await SystemFunction.WriteData("CHAT_LOG", chatLogItem.Code, logName, logText);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public static async Task ReadCutscene(MemoryHandler memoryHandler)
        {
            try
            {
                var cutsceneDetectorPointer = (IntPtr)memoryHandler.Scanner.Locations["CUTSCENE_DETECTOR"];
                int cutsceneFlag = (int)memoryHandler.GetInt64(cutsceneDetectorPointer); // 0 = In cuscene, 1 = Not in cutscene

                if (cutsceneFlag == 0)
                {
                    byte[] rawCutsceneText = GetRealBytes(memoryHandler.GetByteArray(memoryHandler.Scanner.Locations["CUTSCENE_TEXT"], 256));
                    string cutsceneText = XMLCleaner.SanitizeXmlString(ChatEntry.ProcessFullLine("003D", rawCutsceneText)).Trim();

                    if (cutsceneText.Length > 0 && cutsceneText != _lastCutsceneText)
                    {
                        _lastCutsceneText = cutsceneText;
                        await SystemFunction.WriteData("CUTSCENE", "003D", "", cutsceneText);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static byte[] GetRealBytes(byte[] bytes)
        {
            List<byte> bytesList = new List<byte>();
            int nullIndex = bytes.ToList().IndexOf(0x00);

            for (int i = 0; i < nullIndex; i++)
            {
                bytesList.Add(bytes[i]);
            }

            return bytesList.ToArray();
        }

        private static bool IsViewingCutscene(MemoryHandler memoryHandler)
        {
            if (memoryHandler.Reader.CanGetActors())
            {
                var partyMembers = memoryHandler.Reader.GetPartyMembers().PartyMembers.Values;
                var currentPlayer = memoryHandler.Reader.GetCurrentPlayer();
                List<StatusItem> currentPlayerStatusItems = currentPlayer.Entity.StatusItems;

                /*
                if (currentPlayer.Entity.InCutscene)
                {
                    return true;
                }
                */

                // status check (party members)
                foreach (var partyMember in partyMembers)
                {
                    var StatusItems = partyMember.StatusItems;

                    foreach (var statusItem in StatusItems)
                    {
                        if (IsCutsceneStatus(statusItem))
                        {
                            return true;
                        }
                    }
                }

                // status check (current player)
                foreach (var statusItem in currentPlayerStatusItems)
                {
                    if (IsCutsceneStatus(statusItem))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsCutsceneStatus(StatusItem statusItem)
        {
            // knock down
            if (_knockDownNames.Contains(statusItem.StatusName) || _knockDownCodes.Contains(statusItem.StatusID))
            {
                return true;
            }

            // preoccupied
            if (_preoccupiedNames.Contains(statusItem.StatusName) || _preoccupiedCodes.Contains(statusItem.StatusID))
            {
                return true;
            }

            return false;
        }
    }

    class ArrayFunction
    {
        public static bool IsSameChatLogEntries(List<ChatLogItem> chatLogEntries, List<ChatLogItem> lastChatLogEntries)
        {
            if (chatLogEntries.Count != lastChatLogEntries.Count)
            {
                return false;
            }

            for (int i = 0; i < chatLogEntries.Count; i++)
            {
                ChatLogItem chatLogItem = chatLogEntries[i];
                ChatLogItem lastChatLogItem = lastChatLogEntries[i];

                if (chatLogItem.Message != lastChatLogItem.Message)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
