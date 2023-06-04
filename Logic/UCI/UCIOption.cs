﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LTChess.Core
{
    public struct UCIOption
    {
        public const string Opt_DefaultSearchTime = "Default Search Time";
        public const string Opt_DefaultSearchDepth = "Default Search Depth";
        public const string Opt_UseAspirationWindows = "Use Aspiration Windows";
        public const string Opt_AspirationWindowMargin = "Aspiration Window Margin";
        public const string Opt_MarginIncreasePerDepth = "MarginIncreasePerDepth";
        public const string Opt_DefaultMoveOverhead = "Move Overhead";


        public string Name;
        public string Type;
        public string DefaultValue;
        public string MinValue;
        public string MaxValue;

        public UCIOption(string name, string type, string defaultValue, string minValue, string maxValue)
        {
            Name = name;
            Type = type;
            DefaultValue = defaultValue;
            MinValue = minValue;
            MaxValue = maxValue;
        }

        public UCIOption(string name, string type, string defaultValue)
        {
            Name = name;
            Type = type;
            DefaultValue = defaultValue;
        }

        public override string ToString()
        {
            return "option name " + Name + " type " + Type + " default " + DefaultValue + (MinValue.Length == 0 ? string.Empty : (" min " + MinValue + " max " + MaxValue));
        }
    }
}
