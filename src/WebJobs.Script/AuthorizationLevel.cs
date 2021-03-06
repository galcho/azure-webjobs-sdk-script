﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

namespace Microsoft.Azure.WebJobs.Script
{
    public enum AuthorizationLevel
    {
        /// <summary>
        /// Allow access to anonymous requests.
        /// </summary>
        Anonymous = 0,

        /// <summary>
        /// Allow access to requests that include a valid authentication token
        /// </summary>
        User,

        /// <summary>
        /// Allow access to requests that include the function key
        /// </summary>
        Function,

        /// <summary>
        /// Allow access to requests that include the master key
        /// </summary>
        Admin
    }
}
