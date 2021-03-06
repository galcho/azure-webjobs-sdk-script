﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Description;

namespace WebJobs.Script.Tests
{
    public class TestInvoker : IFunctionInvoker
    {
        public int InvokeCount = 0;

        public Task Invoke(object[] parameters)
        {
            Interlocked.Increment(ref InvokeCount);
            return Task.FromResult(0);
        }
    }
}
