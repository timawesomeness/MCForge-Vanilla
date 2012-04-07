﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MCForge.World.Blocks
{
    public class Brick : Block
    {
        public override string Name
        {
            get { return "brick"; }
        }
        public override byte VisibleBlock
        {
            get { return 45; }
        }
    }
}