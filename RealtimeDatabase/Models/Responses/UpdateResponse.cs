﻿using System;
using System.Collections.Generic;
using System.Text;

namespace RealtimeDatabase.Models.Responses
{
    class UpdateResponse : ValidatedResponseBase
    {
        public object UpdatedObject { get; set; }
    }
}