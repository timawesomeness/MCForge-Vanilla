﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MCForge.World.Blocks
{
    public class Dirt : Block
    {
        public override string Name
        {
            get { return "dirt"; }
        }
        public override byte VisibleBlock
        {
            get { return 3; }
        }
    }
}
