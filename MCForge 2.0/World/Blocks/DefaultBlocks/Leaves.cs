﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MCForge.World.Blocks
{
    public class Leaves : Block
    {
        public override string Name
        {
            get { return "leaves"; }
        }
        public override byte VisibleBlock
        {
            get { return 18; }
        }
    }
}
