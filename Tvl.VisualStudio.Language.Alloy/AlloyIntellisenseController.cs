﻿namespace Tvl.VisualStudio.Language.Alloy
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using Tvl.VisualStudio.Language.Intellisense;
    using Microsoft.VisualStudio.Text.Editor;

    internal class AlloyIntellisenseController : IntellisenseController
    {
        public AlloyIntellisenseController(ITextView textView, IntellisenseControllerProvider provider)
            : base(textView, provider)
        {
        }

        public override bool SupportsCompletion
        {
            get
            {
                return true;
            }
        }
    }
}