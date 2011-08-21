﻿namespace Tvl.VisualStudio.Language.Java.Debugger
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using Microsoft.VisualStudio.Debugger.Interop;
    using System.Runtime.InteropServices;

    [ComVisible(true)]
    public class JavaDebugCodeContext : IDebugCodeContext3, IDebugCodeContext2, IDebugMemoryContext2
    {
        #region IDebugMemoryContext2 Members

        public int Add(ulong dwCount, out IDebugMemoryContext2 ppMemCxt)
        {
            throw new NotImplementedException();
        }

        public int Compare(enum_CONTEXT_COMPARE Compare, IDebugMemoryContext2[] rgpMemoryContextSet, uint dwMemoryContextSetLen, out uint pdwMemoryContext)
        {
            throw new NotImplementedException();
        }

        public int GetInfo(enum_CONTEXT_INFO_FIELDS dwFields, CONTEXT_INFO[] pinfo)
        {
            throw new NotImplementedException();
        }

        public int GetName(out string pbstrName)
        {
            throw new NotImplementedException();
        }

        public int Subtract(ulong dwCount, out IDebugMemoryContext2 ppMemCxt)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region IDebugCodeContext2 Members

        public int GetDocumentContext(out IDebugDocumentContext2 ppSrcCxt)
        {
            throw new NotImplementedException();
        }

        public int GetLanguageInfo(ref string pbstrLanguage, ref Guid pguidLanguage)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region IDebugCodeContext3 Members

        public int GetModule(out IDebugModule2 ppModule)
        {
            throw new NotImplementedException();
        }

        public int GetProcess(out IDebugProcess2 ppProcess)
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}