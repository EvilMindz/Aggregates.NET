﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Aggregates.Attributes;
using NServiceBus;

namespace Shared
{
    public interface SaidHello : IEvent
    {
        String User { get; set; }
        String Message { get; set; }
    }
}
