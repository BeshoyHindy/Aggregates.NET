﻿using NServiceBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Aggregates.Messages
{
    internal class Handler :
        IHandleMessagesAsync<Accept>,
        IHandleMessagesAsync<Reject>
    {
        public Task Handle(Accept e) { return Task.FromResult(0); }
        public Task Handle(Reject e) { return Task.FromResult(0); }
    }
}
