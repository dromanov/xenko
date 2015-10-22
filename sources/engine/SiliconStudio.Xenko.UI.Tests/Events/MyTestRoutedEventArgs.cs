﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.
using SiliconStudio.Xenko.UI.Events;

namespace SiliconStudio.Xenko.UI.Tests.Events
{
    internal class MyTestRoutedEventArgs : RoutedEventArgs
    {
        public MyTestRoutedEventArgs(RoutedEvent routedEvent)
            : base(routedEvent)
        {
        }
    }
}