﻿namespace Tvl.VisualStudio.Language.Parsing4
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using Antlr4.Runtime;
    using JetBrains.Annotations;
    using Microsoft.VisualStudio.Text;
    using Microsoft.VisualStudio.Text.Tagging;
    using ClassifierOptions = Tvl.VisualStudio.Language.Parsing.ClassifierOptions;
    using Interval = Antlr4.Runtime.Misc.Interval;

    public abstract class AntlrTaggerBaseOld<TState, TTag> : ITagger<TTag>
        where TState : Tvl.VisualStudio.Language.Parsing.ITextLineState<TState>
        where TTag : ITag
    {
        private readonly ITextBuffer _textBuffer;
        private readonly IEqualityComparer<TState> _stateComparer;
        private readonly ClassifierOptions _options;

        private readonly List<TState> _lineStates = new List<TState>();
        private ITextVersion _lineStatesVersion;

        private ITokenSourceWithState<TState> _lexer;
        private List<Tuple<IToken, TState>> _tokenCache;
        private int _tokenIndex;

        private int? _firstDirtyLine;
        private int? _lastDirtyLine;

        private int? _firstChangedLine;
        private int? _lastChangedLine;

        public AntlrTaggerBaseOld([NotNull] ITextBuffer textBuffer, IEqualityComparer<TState> stateComparer = null, ClassifierOptions options = ClassifierOptions.None)
        {
            Requires.NotNull(textBuffer, nameof(textBuffer));

            _textBuffer = textBuffer;
            _stateComparer = stateComparer ?? EqualityComparer<TState>.Default;
            _options = options;

            ITextSnapshot snapshot = textBuffer.CurrentSnapshot;
            _lineStates.AddRange(Enumerable.Repeat(GetStartState().CreateDirtyState(), snapshot.LineCount));
            _lineStatesVersion = snapshot.Version;
            if ((options & ClassifierOptions.ManualUpdate) == 0)
                SubscribeEvents();

            ForceRetagLines(0, textBuffer.CurrentSnapshot.LineCount - 1);
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<TTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            return spans.SelectMany(GetTags);
        }

        protected abstract TState GetStartState();

        protected abstract ITokenSourceWithState<TState> CreateLexer(SnapshotSpan span, int startLine, TState startState);

        [NotNull]
        protected virtual IEnumerable<ITagSpan<TTag>> GetTags(SnapshotSpan span)
        {
            List<ITagSpan<TTag>> classificationSpans = new List<ITagSpan<TTag>>();

            bool spanExtended = false;

            int extendMultilineSpanToLine = 0;
            SnapshotSpan extendedSpan = span;

            Span requestedSpan = span;
            TState startState = AdjustParseSpan(ref span);

            ITokenSourceWithState<TState> lexer = CreateLexer(span, span.Start.GetContainingLine().LineNumber + 1, startState);

            IToken previousToken = null;
            bool previousTokenEndsLine = false;

            /* this is held outside the loop because only tokens which end at the end of a line
             * impact its value.
             */
            bool lineStateChanged = false;

            _lexer = lexer;
            _tokenCache = new List<Tuple<IToken, TState>>();
            _tokenIndex = -1;

            while (true)
            {
                _tokenIndex++;
                TState stateAfterToken;
                IToken token = PeekToken(0, false, out stateAfterToken);

                bool inBounds = token.StartIndex < span.End.Position;

                int startLineCurrent;
                if (token.Type == IntStreamConstants.Eof)
                    startLineCurrent = span.Snapshot.LineCount;
                else
                    startLineCurrent = token.Line;

                if (previousToken == null || previousToken.Line < startLineCurrent - 1)
                {
                    // endLinePrevious is the line number the previous token ended on
                    int endLinePrevious;
                    if (previousToken != null)
                        endLinePrevious = span.Snapshot.GetLineNumberFromPosition(previousToken.StopIndex + 1);
                    else
                        endLinePrevious = span.Snapshot.GetLineNumberFromPosition(span.Start) - 1;

                    if (startLineCurrent > endLinePrevious + 1)
                    {
                        int firstMultilineLine = endLinePrevious;
                        if (previousToken == null || previousTokenEndsLine)
                            firstMultilineLine++;

                        for (int i = firstMultilineLine; i < startLineCurrent; i++)
                        {
                            if (!_lineStates[i].IsMultiline || lineStateChanged)
                                extendMultilineSpanToLine = i + 1;

                            if (inBounds)
                                SetLineState(i, GetStartState().CreateMultilineState());
                        }
                    }
                }

                if (token.Type == IntStreamConstants.Eof)
                    break;

                previousToken = token;
                previousTokenEndsLine = TokenEndsAtEndOfLine(span.Snapshot, lexer, token);

                if (IsMultilineToken(span.Snapshot, lexer, token))
                {
                    int startLine = span.Snapshot.GetLineNumberFromPosition(token.StartIndex);
                    int stopLine = span.Snapshot.GetLineNumberFromPosition(token.StopIndex + 1);
                    for (int i = startLine; i < stopLine; i++)
                    {
                        if (!_lineStates[i].IsMultiline)
                            extendMultilineSpanToLine = i + 1;

                        if (inBounds)
                            SetLineState(i, GetStartState().CreateMultilineState());
                    }
                }

                bool tokenEndsLine = previousTokenEndsLine;
                if (tokenEndsLine)
                {
                    TState stateAtEndOfLine = stateAfterToken;
                    int line = span.Snapshot.GetLineNumberFromPosition(token.StopIndex + 1);
                    lineStateChanged =
                        _lineStates[line].IsMultiline
                        || !_stateComparer.Equals(_lineStates[line], stateAtEndOfLine);

                    // even if the state didn't change, we call SetLineState to make sure the _first/_lastChangedLine values get updated.
                    if (inBounds)
                        SetLineState(line, stateAtEndOfLine);

                    if (lineStateChanged)
                    {
                        if (line < span.Snapshot.LineCount - 1)
                        {
                            /* update the span's end position or the line state change won't be reflected
                             * in the editor
                             */
                            int endPosition = span.Snapshot.GetLineFromLineNumber(line + 1).EndIncludingLineBreak;
                            if (endPosition > extendedSpan.End)
                            {
                                spanExtended = true;
                                extendedSpan = new SnapshotSpan(extendedSpan.Snapshot, Span.FromBounds(extendedSpan.Start, endPosition));
                            }
                        }
                    }
                }

                if (token.StartIndex >= span.End.Position)
                    break;

                if (token.StopIndex < requestedSpan.Start)
                    continue;

                var tokenClassificationSpans = GetTagSpansForToken(token, span.Snapshot);
                if (tokenClassificationSpans != null)
                    classificationSpans.AddRange(tokenClassificationSpans);

                if (!inBounds)
                    break;
            }

            if (extendMultilineSpanToLine > 0)
            {
                int endPosition = extendMultilineSpanToLine < span.Snapshot.LineCount ? span.Snapshot.GetLineFromLineNumber(extendMultilineSpanToLine).EndIncludingLineBreak : span.Snapshot.Length;
                if (endPosition > extendedSpan.End)
                {
                    spanExtended = true;
                    extendedSpan = new SnapshotSpan(extendedSpan.Snapshot, Span.FromBounds(extendedSpan.Start, endPosition));
                }
            }

            if (spanExtended)
            {
                /* Subtract 1 from each of these because the spans include the line break on their last
                 * line, forcing it to appear as the first position on the following line.
                 */
                int firstLine = extendedSpan.Snapshot.GetLineNumberFromPosition(span.End);
                int lastLine = extendedSpan.Snapshot.GetLineNumberFromPosition(extendedSpan.End) - 1;
                ForceRetagLines(firstLine, lastLine);
            }

            return classificationSpans;
        }

        protected virtual void SetLineState(int line, TState state)
        {
            _lineStates[line] = state;
            if (!state.IsDirty && _firstDirtyLine.HasValue && _firstDirtyLine == line)
            {
                _firstDirtyLine++;
            }

            if (!state.IsDirty && _lastDirtyLine.HasValue && _lastDirtyLine == line)
            {
                _firstDirtyLine = null;
                _lastDirtyLine = null;
            }
        }

        protected virtual TState AdjustParseSpan(ref SnapshotSpan span)
        {
            int start = span.Start.Position;
            int endPosition = span.End.Position;

            ITextSnapshotLine firstDirtyLine = null;
            if (_firstDirtyLine.HasValue)
            {
                firstDirtyLine = span.Snapshot.GetLineFromLineNumber(_firstDirtyLine.Value);
                start = Math.Min(start, firstDirtyLine.Start.Position);
            }

            bool haveState = false;
            TState state = default(TState);
            int startLine = span.Snapshot.GetLineNumberFromPosition(start);
            while (startLine > 0)
            {
                TState lineState = _lineStates[startLine - 1];
                if (!lineState.IsMultiline)
                {
                    haveState = true;
                    state = lineState;
                    break;
                }

                startLine--;
            }

            if (startLine == 0)
            {
                haveState = true;
                state = GetStartState();
            }

            start = span.Snapshot.GetLineFromLineNumber(startLine).Start;
            int length = endPosition - start;
            span = new SnapshotSpan(span.Snapshot, start, length);
            Debug.Assert(haveState);
            return state;
        }

        protected virtual bool TokensSkippedLines(ITextSnapshot snapshot, int endLinePrevious, IToken token)
        {
            int startLineCurrent = snapshot.GetLineNumberFromPosition(token.StartIndex);
            return startLineCurrent > endLinePrevious + 1;
        }

        protected virtual bool IsMultilineToken(ITextSnapshot snapshot, ITokenSourceWithState<TState> lexer, IToken token)
        {
            ITextSnapshotLine line = snapshot.GetLineFromPosition(token.StartIndex);
            return token.StopIndex > line.End;
        }

        protected virtual bool TokenEndsAtEndOfLine(ITextSnapshot snapshot, ITokenSourceWithState<TState> lexer, IToken token)
        {
            ICharStream charStream = lexer.CharStream;
            if (charStream != null)
            {
                int nextCharIndex = token.StopIndex + 1;
                if (nextCharIndex >= charStream.Size)
                    return true;

                int c = charStream.GetText(new Interval(token.StopIndex + 1, token.StopIndex + 1))[0];
                return c == '\r' || c == '\n';
            }

            ITextSnapshotLine line = snapshot.GetLineFromPosition(token.StopIndex + 1);
            return line.End <= token.StopIndex + 1 && line.EndIncludingLineBreak >= token.StopIndex + 1;
        }

        protected virtual void OnTagsChanged([NotNull] SnapshotSpanEventArgs e)
        {
            Requires.NotNull(e, nameof(e));

            var t = TagsChanged;
            if (t != null)
                t(this, e);
        }

        [NotNull]
        protected virtual IEnumerable<ITagSpan<TTag>> GetTagSpansForToken([NotNull] IToken token, [NotNull] ITextSnapshot snapshot)
        {
            Requires.NotNull(token, nameof(token));
            Requires.NotNull(snapshot, nameof(snapshot));

            TTag tag;
            if (TryClassifyToken(token, out tag))
            {
                SnapshotSpan span = new SnapshotSpan(snapshot, token.StartIndex, token.StopIndex - token.StartIndex + 1);
                return new ITagSpan<TTag>[] { new TagSpan<TTag>(span, tag) };
            }

            return Enumerable.Empty<ITagSpan<TTag>>();
        }

        [ContractAnnotation("=> true, tag:notnull")]
        protected virtual bool TryClassifyToken([NotNull] IToken token, out TTag tag)
        {
            Requires.NotNull(token, nameof(token));

            tag = default(TTag);
            return false;
        }

        protected IToken PeekToken(int offset, bool skipOffChannelTokens)
        {
            TState stateAfterToken;
            return PeekToken(offset, skipOffChannelTokens, out stateAfterToken);
        }

        protected virtual IToken PeekToken(int offset, bool skipOffChannelTokens, out TState stateAfterToken)
        {
            stateAfterToken = default(TState);
            int index = _tokenIndex + offset;

            while (index >= 0 && (_tokenCache.Count == 0 || _tokenCache[_tokenCache.Count - 1].Item1.Type != IntStreamConstants.Eof))
            {
                while (index >= _tokenCache.Count)
                {
                    if (_tokenCache.Count != 0 && _tokenCache[_tokenCache.Count - 1].Item1.Type == IntStreamConstants.Eof)
                        break;

                    IToken token = _lexer.NextToken();
                    TState state = _lexer.GetCurrentState();
                    _tokenCache.Add(Tuple.Create(token, state));
                }

                if (index >= _tokenCache.Count)
                    index = _tokenCache.Count - 1;

                IToken t = _tokenCache[index].Item1;
                if (skipOffChannelTokens && t.Channel != Lexer.DefaultTokenChannel)
                {
                    if (offset < 0)
                        index--;
                    else
                        index++;

                    continue;
                }

                stateAfterToken = _tokenCache[index].Item2;
                return t;
            }

            return null;
        }

        protected virtual bool IsMultilineSnapshotSpan(SnapshotSpan span)
        {
            if (span.IsEmpty)
                return false;

            return span.Start.GetContainingLine().LineNumber != span.End.GetContainingLine().LineNumber;
        }

        public virtual void ForceRetagLines(int startLine, int endLine)
        {
            _firstDirtyLine = _firstDirtyLine.HasValue ? Math.Min(_firstDirtyLine.Value, startLine) : startLine;
            _lastDirtyLine = _lastDirtyLine.HasValue ? Math.Max(_lastDirtyLine.Value, endLine) : endLine;

            ITextSnapshot snapshot = _textBuffer.CurrentSnapshot;
            int start = snapshot.GetLineFromLineNumber(startLine).Start;
            int end = snapshot.GetLineFromLineNumber(endLine).EndIncludingLineBreak;
            var e = new SnapshotSpanEventArgs(new SnapshotSpan(_textBuffer.CurrentSnapshot, Span.FromBounds(start, end)));
            OnTagsChanged(e);
        }

        protected virtual void SubscribeEvents()
        {
            _textBuffer.ChangedLowPriority += HandleTextBufferChangedLowPriority;
            _textBuffer.ChangedHighPriority += HandleTextBufferChangedHighPriority;
        }

        protected virtual void UnsubscribeEvents()
        {
            _textBuffer.ChangedLowPriority -= HandleTextBufferChangedLowPriority;
            _textBuffer.ChangedHighPriority -= HandleTextBufferChangedHighPriority;
        }

        protected virtual void HandleTextBufferChangedLowPriority(object sender, TextContentChangedEventArgs e)
        {
            if (e.After == _textBuffer.CurrentSnapshot)
            {
                if (_firstChangedLine.HasValue && _lastChangedLine.HasValue)
                {
                    int startLine = _firstChangedLine.Value;
                    int endLine = Math.Min(_lastChangedLine.Value, e.After.LineCount - 1);

                    _firstChangedLine = null;
                    _lastChangedLine = null;

                    ForceRetagLines(startLine, endLine);
                }
            }
        }

        protected virtual void HandleTextBufferChangedHighPriority(object sender, TextContentChangedEventArgs e)
        {
            foreach (ITextChange change in e.Changes)
            {
                int lineNumberFromPosition = e.After.GetLineNumberFromPosition(change.NewPosition);
                int num2 = e.After.GetLineNumberFromPosition(change.NewEnd);
                if (change.LineCountDelta < 0)
                {
                    _lineStates.RemoveRange(lineNumberFromPosition, Math.Abs(change.LineCountDelta));
                }
                else if (change.LineCountDelta > 0)
                {
                    TState endLineState = _lineStates[lineNumberFromPosition];
                    _lineStates.InsertRange(lineNumberFromPosition, Enumerable.Repeat(endLineState, change.LineCountDelta));
                }

                if (_lastDirtyLine.HasValue && _lastDirtyLine.Value > lineNumberFromPosition)
                {
                    _lastDirtyLine += change.LineCountDelta;
                }

                if (_lastChangedLine.HasValue && _lastChangedLine.Value > lineNumberFromPosition)
                {
                    _lastChangedLine += change.LineCountDelta;
                }

                for (int i = lineNumberFromPosition; i <= num2; i++)
                {
                    _lineStates[i] = _lineStates[i].CreateDirtyState();
                }

                _firstChangedLine = _firstChangedLine.HasValue ? Math.Min(_firstChangedLine.Value, lineNumberFromPosition) : lineNumberFromPosition;
                _lastChangedLine = _lastChangedLine.HasValue ? Math.Max(_lastChangedLine.Value, num2) : num2;
            }

            _lineStatesVersion = e.AfterVersion;
        }
    }
}
