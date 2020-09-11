﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Microsoft.PowerPlatform.Formulas.Tools.Parser
{
    internal class TokenStream
    {
        private readonly string _text;
        private readonly int _charCount;
        private int _currentPos; // Current position.
        private int _currentTokenPos; // The start of the current token.
        private readonly StringBuilder _sb;
        private Stack<int> _indentationLevel; // space count expected per block level

        private Stack<Token> _pendingTokens;

        public TokenStream(string text)
        {
            _sb = new StringBuilder();
            _indentationLevel = new Stack<int>();
            _indentationLevel.Push(0);

            _pendingTokens = new Stack<Token>();

            _text = text;
            _charCount = _text.Length;
        }

        public bool Eof => _currentPos >= _charCount;
        private char CurrentChar => _currentPos < _charCount ? _text[_currentPos] : '\0';

        private char NextChar()
        {
            if (_currentPos >= _charCount)
                throw new InvalidOperationException();

            if (++_currentPos < _charCount)
                return _text[_currentPos];

            _currentPos = _charCount;
            return '\0';
        }

        private char PeekChar(int i)
        {
            if (_currentPos + i >= _charCount)
                throw new InvalidOperationException();
            i += _currentPos;

            return (i < _charCount) ? _text[i] : '\0';
        }

        private void StartToken()
        {
            _currentTokenPos = _currentPos;
        }

        private TokenSpan GetSpan()
        {
            return new TokenSpan(_currentTokenPos, _currentPos);
        }

        public void  ReplaceToken(Token tok)
        {
            _pendingTokens.Push(tok);
        }


        public Token GetNextToken(bool expectedExpression = false)
        {
            for (; ; )
            {
                if (Eof)
                    return null;
                if (_pendingTokens.Any())
                    return _pendingTokens.Pop();

                var tok = Dispatch(expectedExpression);
                if (tok != null)
                    return tok;
            }
        }

        private Token Dispatch(bool expectedExpression)
        {
            StartToken();
            if (expectedExpression)
            {
                return GetExpressionToken();
            }

            var ch = CurrentChar;
            if (IsNewLine(ch))
                return SkipNewLines();
            if (_currentPos > 1 && CharacterUtils.IsLineTerm(_text[_currentPos - 1]))
            {
                var tok = GetIndentationToken();
                if (tok != null)
                    return tok;
            }
            if (CharacterUtils.IsIdentStart(ch))
                return GetIdentOrKeyword();
            if (CharacterUtils.IsSpace(ch))
                return SkipSpaces();

            return GetPunctuator();

            // Add comment support
        }

        private bool IsNewLine(char ch)
        {
            if (ch == '\r')
            {
                if (PeekChar(1) == '\n')
                    NextChar();
                return true;
            }

            return ch == '\n';
        }

        private Token GetExpressionToken()
        {
            if (CurrentChar == ' ')
                NextChar();
            var ch = CurrentChar;
            if (IsNewLine(ch))
                return GetMultiLineExpressionToken();
            else
                return GetSingleLineExpressionToken();
        }

        private Token GetMultiLineExpressionToken()
        {
            NextChar();
            var indentMin = PeekCurrentIndentationLevel();
            if (indentMin < _indentationLevel.Peek())
                return new Token(TokenKind.PAExpression, GetSpan(), _sb.ToString());
            _sb.Length = 0;
            StringBuilder lineBuilder = new StringBuilder();
            var lineIndent = PeekCurrentIndentationLevel();
            var newLine = false;

            while (indentMin <= lineIndent)
            {
                _currentPos += indentMin -1;
                if (newLine)
                    _sb.AppendLine();

                lineBuilder.Length = 0;
                while (!IsNewLine(NextChar()))
                {
                    lineBuilder.Append(CurrentChar);
                }
                _sb.Append(lineBuilder.ToString());
                if (CharacterUtils.IsLineTerm(CurrentChar))
                    NextChar();
                lineIndent = PeekCurrentIndentationLevel();
                newLine = true;
            }
            _currentPos--;
            return new Token(TokenKind.PAExpression, GetSpan(), _sb.ToString());
        }

        private Token GetSingleLineExpressionToken()
        {
            _sb.Length = 0;
            _sb.Append(CurrentChar);
            // Advance to end of line
            while (!IsNewLine(NextChar()))
            {
                _sb.Append(CurrentChar);
            }

            return new Token(TokenKind.PAExpression, GetSpan(), _sb.ToString());
        }

        private Token SkipSpaces()
        {
            while (CharacterUtils.IsSpace(CurrentChar))
            {
                NextChar();
            }
            return null;
        }
        private Token SkipNewLines()
        {
            while (IsNewLine(CurrentChar))
            {
                NextChar();
            }
            return null;
        }

        private Token GetPunctuator()
        {
            int punctuatorLength = 0;
            _sb.Length = 0;
            _sb.Append(CurrentChar);
            TokenKind kind = TokenKind.None;
            string content = "";
            for (; ; )
            {
                content = _sb.ToString();
                if (!TryGetPunctuator(content, out TokenKind maybeKind))
                    break;

                kind = maybeKind;

                ++punctuatorLength;
                _sb.Append(PeekChar(_sb.Length));
            }

            while (punctuatorLength-- > 0)
                NextChar();
            return new Token(kind, GetSpan(), content.Substring(0, _sb.Length-1));
        }

        private bool TryGetPunctuator(string maybe, out TokenKind kind)
        {
            switch (maybe)
            {
                case PAConstants.PropertyDelimiterToken:
                    kind = TokenKind.PropertyStart;
                    return true;
                case PAConstants.ControlTemplateSeparator:
                    kind = TokenKind.TemplateSeparator;
                    return true;
                case PAConstants.ControlVariantSeparator:
                    kind = TokenKind.VariantSeparator;
                    return true;
                default:
                    kind = TokenKind.None;
                    return false;
            }
        }

        private Token GetIdentOrKeyword()
        {
            var tokContents = GetIdentCore(out bool hasDelimiterStart, out bool hasDelimiterEnd);
            var span = GetSpan();

            // Don't parse as keyword if there are delimiters
            if (IsKeyword(tokContents) && !hasDelimiterStart)
            {
                if (tokContents == PAConstants.ControlKeyword)
                    return new Token(TokenKind.Control, span, tokContents);
                else if (tokContents == PAConstants.ComponentKeyword)
                    return new Token(TokenKind.Control, span, tokContents);
                else
                    throw new NotImplementedException("Keyword Token Error");
            }

            if (hasDelimiterStart && !hasDelimiterEnd)
                throw new NotImplementedException("Mismatched Ident Delimiter Error");

            return new Token(TokenKind.Identifier, span, tokContents);
        }

        private string GetIdentCore(out bool hasDelimiterStart, out bool hasDelimiterEnd)
        {
            _sb.Length = 0;
            hasDelimiterStart = CharacterUtils.IsIdentDelimiter(CurrentChar);
            hasDelimiterEnd = false;

            if (!hasDelimiterStart)
            {
                // Simple identifier.
                while (CharacterUtils.IsSimpleIdentCh(CurrentChar))
                {
                    _sb.Append(CurrentChar);
                    NextChar();
                }

                return _sb.ToString();
            }

            // Delimited identifier.
            NextChar();
            int ichStrMin = _currentPos;

            // Accept any characters up to the next unescaped identifier delimiter.
            // String will be corrected in the IdentToken if needed.
            for (; ; )
            {
                if (Eof)
                    break;
                if (CharacterUtils.IsIdentDelimiter(CurrentChar))
                {
                    if (CharacterUtils.IsIdentDelimiter(PeekChar(1)))
                    {
                        // Escaped delimiter.
                        _sb.Append(CurrentChar);
                        NextChar();
                        NextChar();
                    }
                    else
                    {
                        // End of the identifier.
                        NextChar();
                        hasDelimiterEnd = true;
                        break;
                    }
                }
                else if (IsNewLine(CurrentChar))
                {
                    // Terminate an identifier on a new line character
                    // Don't include the new line in the identifier
                    hasDelimiterEnd = false;
                    break;
                }
                else
                {
                    _sb.Append(CurrentChar);
                    NextChar();
                }
            }

            return _sb.ToString();
        }

        private int PeekCurrentIndentationLevel()
        {
            var indentation = 0;
            var ch = CurrentChar;
            while (ch == ' ')
            {
                ++indentation;
                ch = PeekChar(indentation);
            }

            return indentation;
        }

        // Returns null if no indentation change
        private Token GetIndentationToken()
        {
            var currentIndentation = _indentationLevel.Peek();
            var indentation = PeekCurrentIndentationLevel();
            _currentPos += indentation;

            if (indentation == currentIndentation)
                return null;

            if (indentation > currentIndentation)
            {
                _indentationLevel.Push(indentation);
                return new Token(TokenKind.Indent, GetSpan(), _text.Substring(_currentTokenPos, _currentPos - _currentTokenPos));
            }

            // Dedent handling
            while (_indentationLevel.Peek() > indentation)
            {
                _indentationLevel.Pop();
                _pendingTokens.Push(new Token(TokenKind.Dedent, GetSpan(), _text.Substring(_currentTokenPos, _currentPos - _currentTokenPos)));
            }

            if (indentation == _indentationLevel.Peek())
            {
                return _pendingTokens.Pop();
            }

            throw new NotImplementedException( "Dedent mismatch error");
        }
        private static bool IsKeyword(string str)
        {
            return PAConstants.ComponentKeyword == str || PAConstants.ControlKeyword == str;
        }

    }
}