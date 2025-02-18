using Sharlayan;
using Sharlayan.Core;
using Sharlayan.Extensions;
using Sharlayan.Models;
using Sharlayan.Models.ReadResults;
using Sharlayan.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace tataru_assistant_reader
{
    class Program
    {
        private static bool _keepRunning = false;

        static async Task Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }

            while (true)
            {
                try
                {
                    MemoryHandler memoryHandler = SystemFunction.CreateMemoryHandler();
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

            _keepRunning = true;
            await Task.Run(AliveCheck);

            while (_keepRunning)
            {
                if (!memoryHandler.Scanner.IsScanning)
                {
                    await Task.WhenAll(
                        ReaderFunction.ReadDialog(memoryHandler),
                        ReaderFunction.ReadChatLog(memoryHandler),
                        ReaderFunction.ReadCutscene1(memoryHandler),
                        ReaderFunction.ReadCutscene2(memoryHandler)
                        );
                }

                await Task.Delay(50);
            }

            await SystemFunction.WriteSystemMessage("Stop reading...");
        }

        private static async Task AliveCheck()
        {
            while (true)
            {
                Process[] processes = Process.GetProcessesByName("ffxiv_dx11");
                if (processes.Length > 0)
                {
                    _keepRunning = true;
                    await Task.Delay(1000);
                }
                else
                {
                    _keepRunning = false;
                    break;
                }
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

            if (processes.Length <= 0) { throw new Exception("Waiting..."); }

            // Create configuration
            SharlayanConfiguration configuration = new SharlayanConfiguration
            {
                ProcessModel = new ProcessModel
                {
                    Process = processes.FirstOrDefault(),
                },
            };

            // Create memoryHandler
            MemoryHandler memoryHandler = new MemoryHandler(configuration);
            memoryHandler.Scanner.Locations.Clear();

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

            if (type != "SYSTEM")
            {
                name = ChatCleaner.ProcessFullLine(code, Encoding.UTF8.GetBytes(name.Replace("\r", "[r]")));
                text = ChatCleaner.ProcessFullLine(code, Encoding.UTF8.GetBytes(text.Replace("\r", "[r]")));
            }

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

        private static string _lastCutsceneText1 = "";
        private static string _lastCutsceneText2 = "";

        static readonly List<string> _systemCode = new List<string>() { "0039", "0839", "0003", "0038", "003C", "0048", "001D", "001C" };

        public static async Task ReadDialog(MemoryHandler memoryHandler)
        {
            try
            {
                string dialogName = StringFunction.GetMemoryString(memoryHandler, "PANEL_NAME", 128);
                string dialogText = StringFunction.GetMemoryString(memoryHandler, "PANEL_TEXT", 512);

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
                ChatLogResult readResult = memoryHandler.Reader.GetChatLog(_previousArrayIndex, _previousOffset);
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
                        ChatLogItem chatLogItem = chatLogEntries[i];

                        string logName = StringFunction.GetLogName(chatLogItem);
                        string logText = chatLogItem.Message;

                        if (logName.Length == 0 && _systemCode.IndexOf(chatLogItem.Code) < 0)
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

        public static async Task ReadCutscene1(MemoryHandler memoryHandler)
        {
            try
            {
                var cutsceneDetectorPointer = (IntPtr)memoryHandler.Scanner.Locations["CUTSCENE_DETECTOR"];
                int isCutscene = (int)memoryHandler.GetInt64(cutsceneDetectorPointer);

                if (isCutscene == 1) return;

                string cutsceneText1 = StringFunction.GetMemoryString(memoryHandler, "CUTSCENE_TEXT", 256);

                if (cutsceneText1.Length > 0 && cutsceneText1 != _lastCutsceneText1)
                {
                    _lastCutsceneText1 = cutsceneText1;
                    await SystemFunction.WriteData("CUTSCENE1", "003D", "", cutsceneText1);
                }
            }
            catch (Exception)
            {
            }
        }

        public static async Task ReadCutscene2(MemoryHandler memoryHandler)
        {
            try
            {
                string cutsceneText2 = StringFunction.GetMemoryString(memoryHandler, "CUTSCENE_TEXT_2", 256);

                if (cutsceneText2.Length > 0 && cutsceneText2 != _lastCutsceneText2)
                {
                    _lastCutsceneText2 = cutsceneText2;
                    await SystemFunction.WriteData("CUTSCENE2", "003D", "", cutsceneText2, 1000);
                }
            }
            catch (Exception)
            {
            }
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

    class StringFunction
    {
        public static string GetLogName(ChatLogItem chatLogItem)
        {
            string logName = "";

            try
            {
                if (chatLogItem.PlayerName != null)
                {
                    logName = chatLogItem.PlayerName;
                }
            }
            catch (Exception)
            {
            }

            return logName;
        }

        public static string GetMemoryString(MemoryHandler memoryHandler, string key, int length)
        {
            string byteString = "";

            try
            {
                byte[] byteArray = memoryHandler.GetByteArray(memoryHandler.Scanner.Locations[key], length);
                byteArray = GetRealByteArray(byteArray);
                byteString = Encoding.UTF8.GetString(byteArray);
            }
            catch (Exception)
            {
            }

            return byteString;
        }

        public static byte[] GetRealByteArray(byte[] byteArray)
        {
            List<byte> byteList = new List<byte>();
            int nullIndex = byteArray.ToList().IndexOf(0x00);

            for (int i = 0; i < nullIndex; i++)
            {
                byteList.Add(byteArray[i]);
            }

            return byteList.ToArray();
        }
    }

    class ChatCleaner // Sharlayan.Utilites.ChatCleaner.cs
    {
        private const RegexOptions DefaultOptions = RegexOptions.Compiled | RegexOptions.ExplicitCapture;

        //private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static readonly Regex PlayerChatCodesRegex = new Regex(@"^00(0[A-F]|1[0-9A-F])$", DefaultOptions);

        private static readonly Regex PlayerRegEx = new Regex(@"(?<full>\[[A-Z0-9]{10}(?<first>[A-Z0-9]{3,})20(?<last>[A-Z0-9]{3,})\](?<short>[\w']+\.? [\w']+\.?)\[[A-Z0-9]{12}\])", DefaultOptions);

        private static readonly Regex ArrowRegex = new Regex(@"", RegexOptions.Compiled);

        private static readonly Regex HQRegex = new Regex(@"", RegexOptions.Compiled);

        private static readonly Regex NewLineRegex = new Regex(@"[\r\n]+", RegexOptions.Compiled);

        private static readonly Regex NoPrintingCharactersRegex = new Regex(@"[\x00-\x1F]+", RegexOptions.Compiled);

        private static readonly Regex SpecialPurposeUnicodeRegex = new Regex(@"[\uE000-\uF8FF]", RegexOptions.Compiled);

        private static readonly Regex SpecialReplacementRegex = new Regex(@"[�]", RegexOptions.Compiled);

        public static string ProcessFullLine(string code, byte[] bytes)
        {
            string line = HttpUtility.HtmlDecode(Encoding.UTF8.GetString(bytes.ToArray())).Replace("  ", " ");
            try
            {
                List<byte> newList = new List<byte>();
                for (int x = 0; x < bytes.Count(); x++)
                {
                    switch (bytes[x])
                    {
                        case 2:
                            // special in-game replacements/wrappers
                            // 2 46 5 7 242 2 210 3
                            // 2 29 1 3
                            // remove them
                            byte length = bytes[x + 2];
                            int limit = length - 1;
                            if (length > 1)
                            {
                                x = x + 3 + limit;
                            }
                            else
                            {
                                x = x + 4;
                                newList.Add(32);
                                newList.Add(bytes[x]);
                            }

                            break;
                        // unit separator
                        case 31:
                            // TODO: this breaks in some areas like NOVICE chat
                            // if (PlayerChatCodesRegex.IsMatch(code)) {
                            //     newList.Add(58);
                            // }
                            // else {
                            //     newList.Add(31);
                            // }
                            newList.Add(58);
                            if (PlayerChatCodesRegex.IsMatch(code))
                            {
                                newList.Add(32);
                            }

                            break;
                        default:
                            newList.Add(bytes[x]);
                            break;
                    }
                }

                string cleaned = HttpUtility.HtmlDecode(Encoding.UTF8.GetString(newList.ToArray())).Replace("  ", " ");

                newList.Clear();

                // replace right arrow in chat (parsing)
                cleaned = ArrowRegex.Replace(cleaned, "⇒");
                // replace HQ symbol
                cleaned = HQRegex.Replace(cleaned, "[HQ]");
                // replace all Extended special purpose unicode with empty string
                cleaned = SpecialPurposeUnicodeRegex.Replace(cleaned, string.Empty);
                // cleanup special replacement character bytes: 239 191 189
                cleaned = SpecialReplacementRegex.Replace(cleaned, string.Empty);
                // remove new lines
                cleaned = NewLineRegex.Replace(cleaned, string.Empty);
                // remove characters 0-31
                cleaned = NoPrintingCharactersRegex.Replace(cleaned, string.Empty);

                line = cleaned;
            }
            catch (Exception ex)
            {
                // TODO: figure out how to raise exception
            }

            return ProcessName(line);
        }

        private static string ProcessName(string cleaned)
        {
            string line = cleaned;
            try
            {
                // cleanup name if using other settings
                Match playerMatch = PlayerRegEx.Match(line);
                if (playerMatch.Success)
                {
                    string fullName = playerMatch.Groups[1].Value;
                    string firstName = playerMatch.Groups[2].Value.FromHex();
                    string lastName = playerMatch.Groups[3].Value.FromHex();
                    string player = $"{firstName} {lastName}";

                    // remove double placement
                    cleaned = line.Replace($"{fullName}:{fullName}", "•name•");

                    // remove single placement
                    cleaned = cleaned.Replace(fullName, "•name•");
                    switch (Regex.IsMatch(cleaned, @"^([Vv]ous|[Dd]u|[Yy]ou)"))
                    {
                        case true:
                            cleaned = cleaned.Substring(1).Replace("•name•", string.Empty);
                            break;
                        case false:
                            cleaned = cleaned.Replace("•name•", player);
                            break;
                    }
                }

                cleaned = Regex.Replace(cleaned, @"[\r\n]+", string.Empty);
                cleaned = Regex.Replace(cleaned, @"[\x00-\x1F]+", string.Empty);
                line = cleaned;
            }
            catch (Exception ex)
            {
                // TODO: figure out how to raise exception
            }

            return line;
        }
    }
}
