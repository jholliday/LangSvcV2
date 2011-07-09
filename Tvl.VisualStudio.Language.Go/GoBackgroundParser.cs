﻿namespace Tvl.VisualStudio.Language.Go
{
    using System;
    using System.Collections.Generic;
    using Antlr.Runtime;
    using Microsoft.VisualStudio;
    using Microsoft.VisualStudio.Text;
    using Tvl.VisualStudio.Language.Parsing;
    using Tvl.VisualStudio.Shell.OutputWindow;
    using Stopwatch = System.Diagnostics.Stopwatch;

    public class GoBackgroundParser : BackgroundParser
    {
        public GoBackgroundParser(ITextBuffer textBuffer, ITextDocumentFactoryService textDocumentFactoryService, IOutputWindowService outputWindowService)
            : base(textBuffer, textDocumentFactoryService, outputWindowService)
        {
        }

        protected override void ReParseImpl()
        {
            var outputWindow = OutputWindowService.TryGetPane(PredefinedOutputWindowPanes.TvlIntellisense);
            try
            {
                Stopwatch stopwatch = Stopwatch.StartNew();

                var snapshot = TextBuffer.CurrentSnapshot;
                SnapshotCharStream input = new SnapshotCharStream(snapshot);
                GoLexer lexer = new GoLexer(input);
                GoSemicolonInsertionTokenSource tokenSource = new GoSemicolonInsertionTokenSource(lexer);
                CommonTokenStream tokens = new CommonTokenStream(tokenSource);
                GoParser parser = new GoParser(tokens);
                List<ParseErrorEventArgs> errors = new List<ParseErrorEventArgs>();
                parser.ParseError += (sender, e) =>
                    {
                        errors.Add(e);

                        string message = e.Message;

                        ITextDocument document;
                        if (TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out document) && document != null)
                        {
                            string fileName = document.FilePath;
                            var line = snapshot.GetLineFromPosition(e.Span.Start);
                            message = string.Format("{0}({1},{2}): {3}", fileName, line.LineNumber + 1, e.Span.Start - line.Start.Position + 1, message);
                        }

                        if (message.Length > 100)
                            message = message.Substring(0, 100) + " ...";

                        if (outputWindow != null)
                            outputWindow.WriteLine(message);

                        if (errors.Count > 100)
                            throw new OperationCanceledException();
                    };

                var result = parser.compilationUnit();
                OnParseComplete(new AntlrParseResultEventArgs(snapshot, errors, stopwatch.Elapsed, tokens.GetTokens(), result));
            }
            catch (Exception e)
            {
                if (ErrorHandler.IsCriticalException(e))
                    throw;

                try
                {
                    if (outputWindow != null)
                        outputWindow.WriteLine(e.Message);
                }
                catch (Exception ex2)
                {
                    if (ErrorHandler.IsCriticalException(ex2))
                        throw;
                }
            }
        }
    }
}
