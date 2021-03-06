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
using MCForge.Entity;
using MCForge.Interface.Command;
using MCForge.Utils;

namespace CommandDll
{
    public class CmdLastCmd : ICommand
    {
        public string Name { get { return "Last Command"; } }
        public CommandTypes Type { get { return CommandTypes.information; } }
        public string Author { get { return "Arrem"; } }
        public int Version { get { return 1; } }
        public string CUD { get { return ""; } }
        public byte Permission { get { return 80; } }
        public void Use(Player p, string[] args)
        {
            Player who = null;
            if (args.Length == 0) { who = null; }
            else { who = Player.Find(args[0]); }
            if (who == null) { p.SendMessage("Cannot find that player!"); return; }

            who.ExtraData.CreateIfNotExist("LastCmd", "");
            p.SendMessage("Last command " + who.Username + " used is " + who.ExtraData["LastCmd"]);
        }
        public void Help(Player p)
        {
            p.SendMessage("/lastcmd <player> - Shows the last command <player> used.");
            p.SendMessage("Shortcut: /lastcommand");
        }
        public void Initialize()
        {
            Command.AddReference(this, new string[2] { "lastcmd", "lastcommand" });
        }
    }
}
