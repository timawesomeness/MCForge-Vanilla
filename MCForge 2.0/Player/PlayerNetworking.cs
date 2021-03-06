﻿/*
Copyright 2011 MCForge
Dual-licensed under the Educational Community License, Version 2.0 and
the GNU General Public License, Version 3 (the "Licenses"); you may
not use this file except in compliance with the Licenses. You may
obtain a copy of the Licenses at
http://www.opensource.org/licenses/ecl2.php
http://www.gnu.org/licenses/gpl-3.0.html
Unless required by applicable law or agreed to in writing,
software distributed under the Licenses are distributed on an "AS IS"
BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express
or implied. See the Licenses for the specific language governing
permissions and limitations under the Licenses.
*/
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MCForge.API.PlayerEvent;
using MCForge.API.System;
using MCForge.Core;
using MCForge.Groups;
using MCForge.Utilities;
using MCForge.Utilities.Settings;
using MCForge.World;
using System.Drawing;
using MCForge.Utils;

namespace MCForge.Entity {
    public partial class Player {
        #region Incoming Data
        protected static void Incoming(IAsyncResult result) {
            while (!Server.Started)
                Thread.Sleep(100);

            Player p = (Player)result.AsyncState;

            //Why is this here? this is a terrible spot for this
            try {
                if (Server.reviewlist.Contains(p)) {
                    Server.reviewlist.Remove(p);
                    foreach (Player pl in Server.reviewlist.ToArray()) {
                        int position = Server.reviewlist.IndexOf(pl);
                        if (position == 0) {
                            pl.SendMessage("You're next in the review queue!");
                            continue;
                        }
                        pl.SendMessage(position == 1 ? "There is " + position + " players in front of you!" : "There are " + position + " players in front of you!");
                    }
                }
            }
            catch {
                Logger.Log("Error removing " + p.Username + " from the review list!", LogType.Error);
            }

            try {
                int length = p.Socket.EndReceive(result);
                if (length == 0) {
                    p.CloseConnection();
                    if (!p.IsBeingKicked) {
                        var color = (string)p.ExtraData.GetIfExist("Color");
                        UniversalChat(color ?? "" + p.Username + Server.DefaultColor + " has disconnected.");
                    }
                    return;
                }


                byte[] b = new byte[p.buffer.Length + length];
                Buffer.BlockCopy(p.buffer, 0, b, 0, p.buffer.Length);
                Buffer.BlockCopy(p.tempBuffer, 0, b, p.buffer.Length, length);
                p.buffer = p.HandlePacket(b);
                p.Socket.BeginReceive(p.tempBuffer, 0, p.tempBuffer.Length, SocketFlags.None, new AsyncCallback(Incoming), p);
            }
            catch (SocketException) {
                p.CloseConnection();
                return;
            }
            catch (Exception e) {
                p.Kick("Error!");
                Logger.LogError(e);
                return;
            }
        }
        protected byte[] HandlePacket(byte[] buffer) {

            try {
                int length = 0; byte msg = buffer[0];
                // Get the length of the message by checking the first byte
                switch (msg) {
                    case 0: length = 130; break; // login
                    case 2: SMPKick("This is not an SMP Server!"); break; // SMP Handshake packet
                    case 5: length = 8; break; // blockchange
                    case 8: length = 9; break; // input
                    case 13: length = 65; break; // chat
                    default: {
                            OnReceivePacket args = new OnReceivePacket(this, buffer);
                            if (args.IsCanceled)
                                return new byte[1];
                            Kick("Unhandled message id \"" + msg + "\"!");
                            return new byte[0];
                        }

                }
                if (buffer.Length > length) {
                    byte[] message = new byte[length];
                    Buffer.BlockCopy(buffer, 1, message, 0, length);

                    byte[] tempbuffer = new byte[buffer.Length - length - 1];
                    Buffer.BlockCopy(buffer, length + 1, tempbuffer, 0, buffer.Length - length - 1);

                    buffer = tempbuffer;

                    ThreadPool.QueueUserWorkItem(delegate {
                        switch (msg) {
                            case 0: HandleLogin(message); break;
                            case 5: HandleBlockchange(message); break;
                            case 8: HandleIncomingPos(message); break;
                            case 13: HandleChat(message); break;
                        }
                    });

                    if (buffer.Length > 0)
                        buffer = HandlePacket(buffer);
                    else
                        return new byte[0];


                }
            }
            catch (Exception e) {
                Kick("CONNECTION ERROR: (0x03)");
                Logger.Log("[ERROR]: PLAYER MESSAGE RECIEVE ERROR (0x03)", Color.Red, Color.Black);
                Logger.LogError(e);
            }
            return buffer;
        }

        protected void HandleLogin(byte[] message) {
            try {
                if (IsLoggedIn) return;
                byte version = message[0];
                Username = enc.GetString(message, 1, 64).Trim();
                string BanReason = null;
                bool banned = false;

                foreach (string line in File.ReadAllLines("bans/BanInfo.txt")) { if (Username == line.Split('`')[0]) { BanReason = line.Split('`')[1]; } }
                foreach (string line in Server.IPBans) { if (line == Ip) banned = true; }
                foreach (string line in Server.UsernameBans) { if (line == Username) banned = true; }
                if (banned) { if (BanReason == "No reason given.") { SKick("You are banned because " + BanReason); } else { SKick("You are banned!"); } }
                string verify = enc.GetString(message, 65, 32).Trim();
                byte type = message[129];
                if (!VerifyAccount(Username, verify)) return;
                if (Server.Verifying) IsVerified = false;
                else IsVerified = true;
                if (version != ServerSettings.Version) { SKick("Wrong Version!"); return; }
                try {
                    Server.TempBan tb = Server.tempbans.Find(ban => ban.name.ToLower() == Username.ToLower());
                    if (DateTime.Now > tb.allowed) {
                        Server.tempbans.Remove(tb);
                    }
                    else {
                        SKick("You're still tempbanned!");
                        return;
                    }
                }
                catch {
                }

                bool cancel = OnPlayerConnect.Call(this);
                if (cancel) {
                    Kick("Disconnected by event");
                    return;
                }

                //TODO Database Stuff

                Logger.Log("[System]: " + Ip + " logging in as " + Username + ".", Color.Green, Color.Black);
                UniversalChat(Username + " joined the game!");

                CheckDuplicatePlayers(Username);
                foreach (PlayerGroup g in PlayerGroup.groups)
                    if (g.players.Contains(Username.ToLower()))
                        group = g;

                SendMotd();
                IsLoading = true;
                Level = Server.Mainlevel;
                //SendMap(); changing the level value will send the map
                IsLoggedIn = true;

                id = FreeId();
                UpgradeConnectionToPlayer();

                short x = (short)((0.5 + Level.SpawnPos.x) * 32);
                short y = (short)((1 + Level.SpawnPos.y) * 32);
                short z = (short)((0.5 + Level.SpawnPos.z) * 32);

                Pos = new Vector3(x, z, y);
                Rot = Level.SpawnRot;
                oldPos = Pos;
                oldRot = Rot;

                SpawnThisPlayerToOtherPlayers();
                SpawnOtherPlayersForThisPlayer();
                SendSpawn(this);

                IsLoading = false;

                foreach (string w in ServerSettings.GetSetting("welcomemessage").Split(new string[] { "<br>" }, StringSplitOptions.RemoveEmptyEntries))
                    SendMessage(w);

            }
            catch (Exception e) {
                Logger.LogError(e);
            }
        }
        protected void HandleBlockchange(byte[] message) {
            if (!IsLoggedIn) return;

            ushort x = packet.NTHO(message, 0);
            ushort y = packet.NTHO(message, 2);
            ushort z = packet.NTHO(message, 4);
            byte action = message[6];
            byte newType = message[7];

            LastClick = new Vector3(x, y, z);

            if (newType > 49 || (newType == 7 && !IsAdmin)) {
                Kick("HACKED CLIENT!");
                //TODO Send message to op's for adminium hack
                return;
            }

            byte currentType = Level.GetBlock(x, z, y);
            if (!Block.IsValidBlock(currentType)) {
                Kick("HACKED CLIENT!");
                return;
            }

            bool placing = (action == 1);
            bool canceled = OnPlayerBlockChange.Call(x, y, z, (placing ? ActionType.Place : ActionType.Delete), this, newType);
            if (canceled) // If any event canceled us
                return;
            if (blockChange != null) {
                SendBlockChange(x, z, y, currentType);

                BlockChangeDelegate tempBlockChange = blockChange;
                if (!ExtraData.ContainsKey("PassBackData"))
                    ExtraData.Add("PassBackData", null);

                object tempPassBack = ExtraData["PassBackData"];

                blockChange = null;
                ExtraData["PassBackData"] = null;

                ThreadPool.QueueUserWorkItem(delegate {
                    tempBlockChange.Invoke(this, x, z, y, newType, placing, tempPassBack);
                });
                return;
            }

            if (action == 0) //Deleting
            {
                Level.BlockChange(x, z, y, 0);
            }
            else //Placing
            {
                Level.BlockChange(x, z, y, newType);
            }
        }
        protected void HandleIncomingPos(byte[] message) {
            if (!IsLoggedIn)
                return;

            byte thisid = message[0];

            if (thisid != 0xFF && thisid != id && thisid != 0) {
                //TODO Player.GlobalMessageOps("Player sent a malformed packet!");
                Kick("Hacked Client!");
                return;
            }

            ushort x = packet.NTHO(message, 1);
            ushort y = packet.NTHO(message, 3);
            ushort z = packet.NTHO(message, 5);
            byte rotx = message[7];
            byte roty = message[8];
            if (!(Pos.x == x && Pos.y == y && Pos.z == z)) {
                bool cancel = OnPlayerMove.Call(this, Pos);
                if (cancel) {
                    this.SendToPos(Pos, Rot);
                    return;
                }
            }
            Pos.x = (short)x;
            Pos.y = (short)y;
            Pos.z = (short)z;
            Rot = new byte[2] { rotx, roty };
        }
        protected void HandleChat(byte[] message) {
            if (!IsLoggedIn) return;

            string incomingText = enc.GetString(message, 1, 64).Trim();

            bool canceled = OnPlayerChat.Call(this, incomingText).cancel; //...wat?
            if (canceled)
                return;

            byte incomingID = message[0];
            if (incomingID != 0xFF && incomingID != id && incomingID != 0) {
                Player.UniversalChatOps("Player " + Username + ", sent a malformed packet!");
                Kick("Hacked Client!");
                return;
            }

            incomingText = Regex.Replace(incomingText, @"\s\s+", " ");

            if (StringUtils.ContainsBadChar(incomingText)) {
                Kick("Illegal character in chat message!");
                return;
            }

            if (incomingText.Length == 0)
                return;
            //Fixes crash
            if (incomingText[0] == '/' && incomingText.Length == 1) {
                SendMessage("You didn't specify a command!");
                return;
            }

            //Get rid of whitespace
            var gex = new Regex(@"[ ]{2,}", RegexOptions.None);
            incomingText = gex.Replace(incomingText, @" ");

            //This allows people to use //Command and have it appear as /Command in the chat.
            if (incomingText.StartsWith("//")) {
                incomingText = incomingText.Remove(0, 1);
            }
            else if (incomingText[0] == '/') {
                incomingText = incomingText.Remove(0, 1);

                string[] args = incomingText.Split(' ');
                HandleCommand(args);
                return;
            }

            //Meep is used above for //Command

            FileUtils.CreateFileIfNotExist("text/badwords.txt");
            FileUtils.CreateFileIfNotExist("text/replacementwords.txt");

            string textz = File.ReadAllText("text/replacementwords.txt");
            if (textz == "") {
                File.WriteAllText("text/replacementwords.txt", "Pepper");
            }

            var w = File.ReadAllLines("text/replacementwords.txt");
            foreach (var str in w)
                replacement.Add(str);

            string[] badwords = File.ReadAllLines("text/badwords.txt");
            string[] replacementwords = File.ReadAllLines("text/replacementwords.txt");

            foreach (string word in badwords) {

                if (incomingText.Contains(word))
                    incomingText = Regex.Replace(incomingText, word, replacement[playerRandom.Next(0, replacement.Count)]);

            }

            ExtraData.CreateIfNotExist("Muted", false);
            var isMuted = (bool)ExtraData.GetIfExist("Muted");
            if (isMuted) {
                SendMessage("You are muted!");
                return;
            }

            ExtraData.CreateIfNotExist("Voiced", true);
            var isVoiced = (bool)ExtraData.GetIfExist("Voiced");
            if (Server.moderation && !isVoiced && !Server.devs.Contains(Username)) {
                SendMessage("You can't talk during chat moderation!");
                return;
            }

            ExtraData.CreateIfNotExist("Jokered", false);
            var isJokered = (bool)ExtraData.GetIfExist("Jokered");
            if (isJokered) {
                int a = playerRandom.Next(0, Server.jokermessages.Count);
                incomingText = Server.jokermessages[a];
            }

            //Message appending stuff.
            if (ServerSettings.GetSettingBoolean("messageappending")) {
                if (!String.IsNullOrWhiteSpace(_storedMessage)) {
                    if (!incomingText.EndsWith(">") && !incomingText.EndsWith("<")) {
                        incomingText = _storedMessage.Replace("|>|", " ").Replace("|<|", " ") + incomingText;
                        _storedMessage = String.Empty;
                    }
                }
                if (incomingText.EndsWith(">")) {
                    _storedMessage += incomingText.Replace(">", "|>|");
                    SendMessage("Message appended!");
                    return;
                }
                else if (incomingText.EndsWith("<")) {
                    _storedMessage += incomingText.Replace("<", "|<|");
                    SendMessage("Message appended!");
                    return;
                }  
            }

            //TODO: add this to a different plugin, its a mess right here, and i hate it
            if (Server.voting) {
                if (Server.kickvote && Server.kicker == this) {
                    SendMessage("You're not allowed to vote!");
                    return;
                }

                ExtraData.CreateIfNotExist("Voted", false);
                var didVote = (bool)ExtraData.GetIfExist("Voted");
                if (didVote) {
                    SendMessage("You have already voted...");
                    return;
                }
                string vote = incomingText.ToLower();
                if (vote == "yes" || vote == "y") { 
                    Server.YesVotes++; 
                    ExtraData["Voted"] = true; 
                    SendMessage("Thanks for voting!"); 
                    return; 
                }
                else if (vote == "no" || vote == "n") { 
                    Server.NoVotes++; 
                    ExtraData["Voted"] = true; 
                    SendMessage("Thanks for voting!"); 
                    return; 
                }
                else { 
                    SendMessage("Use either %aYes " + Server.DefaultColor + "or %cNo " + Server.DefaultColor + " to vote!");
                }

            }

            ExtraData.CreateIfNotExist("OpChat", false);
            if (incomingText[0] == '#' || (bool)ExtraData.GetIfExist("OpChat")) //Opchat ouo
            {
                incomingText = incomingText.Trim().TrimStart('#');
                UniversalChatOps("&a<&fTo Ops&a> " + group.color + Username + ": &f" + incomingText);
                if (group.permission < Server.opchatperm) {
                    SendMessage("&a<&fTo Ops&a> " + group.color + Username + ": &f" + incomingText);
                } //So players who aren't op see their messages
                Logger.Log("<OpChat> <" + Username + "> " + incomingText);
                return;
            }
            if (incomingText[0] == '*') //Rank chat
            {
                string groupname = group.name;
                incomingText = incomingText.Trim().TrimStart('*');
                if (!groupname.EndsWith("ed") && !groupname.EndsWith("s")) {
                    groupname += "s";
                } //Plural
                RankChat(this, "&a<&fTo " + groupname + "&a> " + group.color + Username + ": &f" + incomingText);
                Logger.Log("<" + groupname + " Chat> <" + Username + "> " + incomingText);
                return;
            }
            if (incomingText[0] == '!') //Level chat
            {
                incomingText = incomingText.Trim().TrimStart('!');
                LevelChat(this, "&a<&f" + Level.Name + "&a> " + Username + ":&f " + incomingText);
                Logger.Log("<" + Level.Name + " Chat> " + Username + ": " + incomingText);
                return;
            }

            ExtraData.CreateIfNotExist("AdminChat", false);
            if (incomingText[0] == '+' || (bool)ExtraData.GetIfExist("AdminChat")) //Admin chat
            {
                incomingText = incomingText.TrimStart().TrimStart('+');
                UniversalChatAdmins("&a<&fTo Admins&a> " + group.color + Username + ": &f" + incomingText);
                if (group.permission < Server.adminchatperm) {
                    SendMessage("&a<&fTo Admins&a> " + group.color + Username + ": &f" + incomingText);
                }
                Logger.Log("<AdminChat> <" + Username + "> " + incomingText);
                return;
            }

            ExtraData.CreateIfNotExist("Whispering", false);
            if ((bool)ExtraData.GetIfExist("Whispering")) // /whisper command
            {
                ExtraData.CreateIfNotExist("WhisperTo", null);
                Player to = (Player)ExtraData.GetIfExist("WhisperTo");
                if (to == null) { 
                    SendMessage("Player not found!"); 
                    return; 
                }
                if (to == this) { 
                    SendMessage("Trying to talk to yourself huh?"); 
                    return; 
                }

                SendMessage("[>] <" + to.Username + ">&f " + incomingText);
                to.SendMessage("[<] " + Username + ":&f " + incomingText);
                return;
            }
            if (incomingText[0] == '@') //Whisper whisper woosh woosh
            {
                incomingText = incomingText.Trim();
                if (incomingText[1] == ' ') {
                    incomingText = incomingText.Remove(1, 1);
                }

                incomingText = incomingText.Remove(0, 1);
                Player to = Player.Find(incomingText.Split(' ')[0]);
                incomingText = incomingText.Remove(0, to.Username.Length);
                if (to == null) {
                    SendMessage("Player not found!");
                    return;
                }
                SendMessage("[>] <" + to.Username + ">&f " + incomingText.Trim());
                to.SendMessage("[<] " + Username + ":&f " + incomingText.Trim());
                return;
            }
            //TODO: remove to place better
            Logger.Log("<" + Username + "> " + incomingText);
            var voiceString = (string)ExtraData.GetIfExist("VoiceString") ?? "";
            var mColor = (string)ExtraData.GetIfExist("Color") ?? group.color;
            var mPrefix = (string)ExtraData.GetIfExist("Prefix") ?? "";
            string msg = voiceString +
                          mColor +
                          mPrefix +
                          Username +
                          ": &f" +
                          incomingText;
            UniversalChat(msg);
        }

        public void SetPrefix() {
            ExtraData.CreateIfNotExist("Prefix", "");
            var mTitle = ExtraData.GetIfExist("Title");
            var mTColor = ExtraData.GetIfExist("TitleColor");
            var mColor = ExtraData.GetIfExist("Color");
            ExtraData["Prefix"] = mTitle == null ? "" : "[" + mTColor ?? Server.DefaultColor + mTitle + mColor ?? Server.DefaultColor + "]";
        }

        #endregion
        #region Outgoing Packets
        protected void SendPacket(packet pa) {
            try {
                lastPacket = (packet.types)pa.bytes[0];
            }
            catch (Exception e) { Logger.LogError(e); }
            for (int i = 0; i < 3; i++) {
                try {
                    Socket.BeginSend(pa.bytes, 0, pa.bytes.Length, SocketFlags.None, delegate(IAsyncResult result) { }, null);

                    return;
                }
                catch {
                    continue;
                }
            }
            CloseConnection();
        }
        protected void SendMessage(byte PlayerID, string message) {
            packet pa = new packet();


            for (int i = 0; i < 10; i++) {
                message = message.Replace("%" + i, "&" + i);
                message = message.Replace("&" + i + " &", "&");
            }
            for (char ch = 'a'; ch <= 'f'; ch++) {
                message = message.Replace("%" + ch, "&" + ch);
                message = message.Replace("&" + ch + " &", "&");
            }
            message = Server.DefaultColor + message;

            pa.Add(packet.types.Message);
            pa.Add(PlayerID);

            try {
                foreach (string line in LineWrapping(message)) {
                    if (pa.bytes.Length < 64)
                        pa.Add(line, 64);
                    else
                        pa.Set(2, line, 64);

                    SendPacket(pa);
                }
            }
            catch (Exception e) {
                Logger.LogError(e);
            }

        }
        protected void SendMotd() {
            SendPacket(IsAdmin ? MOTD_Admin : MOTD_NonAdmin);
        }
        protected void SendMap() {
            try {
                SendPacket(mapSendStartPacket); //Send the pre-fab map start packet

                packet pa = new packet(); //Create a packet to handle the data for the map
                pa.Add(Level.TotalBlocks); //Add the total amount of blocks to the packet
                byte[] blocks = new byte[Level.TotalBlocks]; //Temporary byte array so we dont have to keep modding the packet array

                byte block; //A block byte outside the loop, we save cycles by not making this for every loop iteration
                Level.ForEachBlock(pos => {
                    //Here we loop through the whole map and check/convert the blocks as necesary
                    //We then add them to our blocks array so we can send them to the player
                    block = Level.Data[pos];
                    //TODO ADD CHECKING
                    blocks[pos] = block;
                });

                pa.Add(blocks); //add the blocks to the packet
                pa.GZip(); //GZip the packet

                int number = (int)Math.Ceiling(((double)(pa.bytes.Length)) / 1024); //The magic number for this packet

                for (int i = 1; pa.bytes.Length > 0; ++i) {
                    short length = (short)Math.Min(pa.bytes.Length, 1024);
                    byte[] send = new byte[1027];
                    packet.HTNO(length).CopyTo(send, 0);
                    Buffer.BlockCopy(pa.bytes, 0, send, 2, length);
                    byte[] tempbuffer = new byte[pa.bytes.Length - length];
                    Buffer.BlockCopy(pa.bytes, length, tempbuffer, 0, pa.bytes.Length - length);
                    pa.bytes = tempbuffer;
                    send[1026] = (byte)(i * 100 / number);

                    packet Send = new packet(send);
                    Send.AddStart(new byte[1] { (byte)packet.types.MapData });

                    SendPacket(Send);
                }

                pa = new packet();
                pa.Add(packet.types.MapEnd);
                pa.Add((short)Level.Size.x);
                pa.Add((short)Level.Size.y);
                pa.Add((short)Level.Size.z);
                SendPacket(pa);

                IsLoading = false;
            }
            catch (Exception e) {
                Logger.LogError(e);
            }
        }
        public void SendSpawn(Player p) {
            byte ID = 0xFF;
            if (p != this)
                ID = p.id;

            packet pa = new packet();
            pa.Add(packet.types.SendSpawn);
            pa.Add((byte)ID);
            pa.Add(p.Username, 64);
            pa.Add(p.Pos.x);
            pa.Add(p.Pos.y);
            pa.Add(p.Pos.z);
            pa.Add(p.Rot);
            SendPacket(pa);
        }
        /// <summary>
        /// This send a blockchange to the player only. (Not other players)
        /// </summary>
        /// <param name="x"></param> The position the block will be placed in (x)
        /// <param name="z"></param> The position the block will be placed in (z)
        /// <param name="y"></param> The position the block will be placed in (y)
        /// <param name="type"></param> The type of block that will be placed.
        public void SendBlockChange(ushort x, ushort z, ushort y, byte type) {
            if (x < 0 || y < 0 || z < 0 || x >= Level.Size.x || y >= Level.Size.y || z >= Level.Size.z) return;

            packet pa = new packet();
            pa.Add(packet.types.SendBlockchange);
            pa.Add(x);
            pa.Add(y);
            pa.Add(z);

            //if (type > 49) type = Block.CustomBlocks[type].VisibleType;
            pa.Add(type);

            SendPacket(pa);
        }
        protected void SendKick(string message) {

            packet pa = new packet();
            pa.Add(packet.types.SendKick);
            pa.Add(message, 64);
            SendPacket(pa);
        }
        protected void SMPKick(string a) {
            //Read first, then kick
            var Stream = Client.GetStream();
            var Reader = new BinaryReader(Stream);
            var Writer = new BinaryWriter(Stream);
            short len = IPAddress.HostToNetworkOrder(Reader.ReadInt16());

            if (len > 1 && len < 17) {
                string name = Encoding.BigEndianUnicode.GetString(Reader.ReadBytes(len * 2));
                Logger.Log(String.Format("{0} tried to log in from an smp client", name));

                byte[] messageInBytes = Encoding.BigEndianUnicode.GetBytes(a);
                Writer.Write((byte)255);
                Writer.Write((short)messageInBytes.Length);
                Writer.Write(messageInBytes);
                Writer.Flush();
                CloseConnection();
            }
            else {
                Logger.Log("Received unknown packet");
                Kick("Unknown Packet received");
            }



        }
        protected void SendPing() {
            SendPacket(pingPacket);
        }

        /// <summary>
        /// Send this player a message
        /// </summary>
        /// <param name="message">The message to send</param>
        public void SendMessage(string message) {
            SendMessage(id, message); // 0xFF is NOT a valid player ID
        }
        /// <summary>
        /// Exactly what the function name is, it might be useful to change this players pos first ;)
        /// </summary>
        public void SendThisPlayerTheirOwnPos() {
            packet pa = new packet();
            pa.Add((byte)8);
            pa.Add(Pos.x);
            pa.Add(Pos.y);
            pa.Add(Pos.z);
            pa.Add(Rot);
            SendPacket(pa);
        }
        /// <summary>
        /// Kick this player with the specified message, the message broadcasts across the server
        /// </summary>
        /// <param name="message">The message to send</param>
        public void Kick(string message) {
            //GlobalMessage(message);
            IsBeingKicked = true;
            SKick(message);
        }
        /// <summary>
        /// Kick this player with a specified message, this message will only get sent to op's
        /// </summary>
        /// <param name="message">The message to send</param>
        public void SKick(string message) {
            Logger.Log("[Info]: Kicked: *" + Username + "* " + message, Color.Yellow, Color.Black);
            SendKick(message);
            //CloseConnection();
        }
        /// <summary>
        /// Sends the specified player to the specified coordinates.
        /// </summary>
        /// <param name="_pos"></param>Vector3 coordinate to send to.
        /// <param name="_rot"></param>Rot to send to.
        public void SendToPos(Vector3 _pos, byte[] _rot) {
            oldPos = Pos; oldRot = Rot;
            _pos.x = (_pos.x < 0) ? (short)32 : (_pos.x > Level.Size.x * 32) ? (short)(Level.Size.x * 32 - 32) : (_pos.x > 32767) ? (short)32730 : _pos.x;
            _pos.z = (_pos.z < 0) ? (short)32 : (_pos.z > Level.Size.z * 32) ? (short)(Level.Size.z * 32 - 32) : (_pos.z > 32767) ? (short)32730 : _pos.z;
            _pos.y = (_pos.y < 0) ? (short)32 : (_pos.y > Level.Size.y * 32) ? (short)(Level.Size.y * 32 - 32) : (_pos.y > 32767) ? (short)32730 : _pos.y;


            packet pa = new packet();
            pa.Add(packet.types.SendTeleport);
            pa.Add(unchecked((byte)-1)); //If the ID is not greater than one it doesn't work :c
            pa.Add(_pos.x);
            pa.Add(_pos.y);
            pa.Add(_pos.z);
            pa.Add(Rot);

            Server.ForeachPlayer(delegate(Player p) {
                if (p.Level == Level && p.IsLoggedIn && !p.IsLoading) {
                    p.SendPacket(pa);
                }
            });
        }

        protected void UpdatePosition(bool ForceTp) {
            byte changed = 0;   //Denotes what has changed (x,y,z, rotation-x, rotation-y)

            Vector3 tempOldPos = oldPos;
            Vector3 tempPos = Pos;
            byte[] tempRot = Rot;
            byte[] tempOldRot = oldRot;

            oldPos = Pos;
            oldRot = Rot;

            int diffX = tempPos.x - tempOldPos.x;
            int diffZ = tempPos.z - tempOldPos.z;
            int diffY = tempPos.y - tempOldPos.y;
            int diffR0 = tempRot[0] - tempRot[0];
            int diffR1 = tempRot[1] - tempRot[1];

            if (ForceTp) 
                changed = 4;
            else {
                //TODO rewrite local pos change code
                if (diffX == 0 && diffY == 0 && diffZ == 0 && diffR0 == 0 && diffR1 == 0) {
                    return; //No changes
                }
                if (Math.Abs(diffX) > 100 || Math.Abs(diffY) > 100 || Math.Abs(diffZ) > 100) {
                    changed = 4; //Teleport Required
                }
                else if (diffR0 == 0 && diffR1 == 0) {
                    changed = 1; //Pos Update Required
                }
                else {
                    changed += 2; //Rot Update Required

                    if (diffX != 0 || diffY != 0 || diffZ != 0) {
                        changed += 1;
                    }
                }
            }

            packet pa = new packet();

            switch (changed) {
                case 1: //Pos Change
                    pa.Add(packet.types.SendPosChange);
                    pa.Add(id);
                    pa.Add((sbyte)(diffX));
                    pa.Add((sbyte)(diffY));
                    pa.Add((sbyte)(diffZ));
                    break;
                case 2: //Rot Change
                    pa.Add(packet.types.SendRotChange);
                    pa.Add(id);
                    pa.Add(new byte[2] { (byte)diffR0, (byte)diffR1 });
                    break;
                case 3: //Pos AND Rot Change
                    pa.Add(packet.types.SendPosANDRotChange);
                    pa.Add(id);
                    pa.Add(diffX);
                    pa.Add(diffY);
                    pa.Add(diffZ);
                    pa.Add(new byte[2] { (byte)diffR0, (byte)diffR1 });
                    break;
                case 4: //Teleport Required
                    pa.Add(packet.types.SendTeleport);
                    pa.Add(id);
                    pa.Add(tempPos.x);
                    pa.Add(tempPos.y);
                    pa.Add(tempPos.z);
                    pa.Add(Rot);
                    break;
            }

            Server.ForeachPlayer(delegate(Player p) {
                if (p != this && p.Level == Level && p.IsLoggedIn && !p.IsLoading) {
                    p.SendPacket(pa);
                }
            });
        }
        #endregion

        /// <summary>
        /// Spawns this player to all other players in the server.
        /// </summary>
        protected void SpawnThisPlayerToOtherPlayers() {
            Server.ForeachPlayer(delegate(Player p) {
                if (p != this)
                    p.SendSpawn(this);
            });
        }
        /// <summary>
        /// Spawns all other players of the server to this player.
        /// </summary>
        protected void SpawnOtherPlayersForThisPlayer() {
            Server.ForeachPlayer(delegate(Player p) {
                if (p != this)
                    SendSpawn(p);
            });
        }

        internal static void GlobalBlockchange(Level l, ushort x, ushort z, ushort y, byte block) {
            Server.ForeachPlayer(delegate(Player p) {
                if (p.Level == l)
                    p.SendBlockChange(x, z, y, block);
            });
        }
        /// <summary>
        /// Kill this player for everyone.
        /// </summary>
        public void GlobalDie() {
            packet pa = new packet(new byte[2] { (byte)packet.types.SendDie, id });
            Server.ForeachPlayer(p => {
                if (p != this) {
                    p.SendPacket(pa);
                }
            });
        }
        /// <summary>
        /// Send a message to everyone, on every world
        /// </summary>
        /// <param name="text">The message to send.</param>
        public static void UniversalChat(string text) {
            Server.ForeachPlayer(p => p.SendMessage(text));
        }
        /// <summary>
        /// Sends a message to all operators+
        /// </summary>
        /// <param name="message">The message to send</param>
        public static void UniversalChatOps(string message) {
            Server.ForeachPlayer(p => {
                if (p.group.permission >= Server.opchatperm) {
                    p.SendMessage(message);
                }
            });
        }
        /// <summary>
        /// Sends a message to all admins+
        /// </summary>
        /// <param name="message">The message to be sent</param>
        public static void UniversalChatAdmins(string message) {
            Server.ForeachPlayer(p => {
                if (p.group.permission >= Server.adminchatperm) {
                    p.SendMessage(message);
                }
            });
        }
        /// <summary>
        /// Sends a message to all of the players with the same rank
        /// </summary>
        /// <param name="from">The player sending the message</param>
        /// <param name="message">The message to send</param>
        public static void RankChat(Player from, string message) {
            Server.ForeachPlayer(delegate(Player p) {
                if (p.group.permission == from.group.permission) {
                    p.SendMessage(message);
                }
            });
        }
        /// <summary>
        /// Sends a message to all of the players on the specified level
        /// </summary>
        /// <param name="from">The player sending the message</param>
        /// <param name="message">The message to be sent</param>
        public static void LevelChat(Player from, string message) {
            Server.ForeachPlayer(delegate(Player p) {
                if (p.Level == from.Level) { p.SendMessage(message); }
            });
        }
        protected void CloseConnection() {
            OnPlayerDisconnect.Call(this, lastPacket.ToString());

            IsLoggedIn = false;

            GlobalDie();
            Logger.Log("[System]: " + Username + " Has DC'ed (" + lastPacket + ")", Color.Gray, Color.Black);

            Server.RemovePlayer(this);
            Server.Connections.Remove(this);

            Socket.Close();
        }

        internal static void GlobalPing() {
            Server.ForeachPlayer(p => p.SendPing());
        }

    }
}
