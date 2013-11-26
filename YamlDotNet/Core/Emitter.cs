//  This file is part of YamlDotNet - A .NET library for YAML.
//  Copyright (c) 2008, 2009, 2010, 2011, 2012, 2013 Antoine Aubry
    
//  Permission is hereby granted, free of charge, to any person obtaining a copy of
//  this software and associated documentation files (the "Software"), to deal in
//  the Software without restriction, including without limitation the rights to
//  use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
//  of the Software, and to permit persons to whom the Software is furnished to do
//  so, subject to the following conditions:
    
//  The above copyright notice and this permission notice shall be included in all
//  copies or substantial portions of the Software.
    
//  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//  SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core.Events;
using ParsingEvent = YamlDotNet.Core.Events.ParsingEvent;
using TagDirective = YamlDotNet.Core.Tokens.TagDirective;
using VersionDirective = YamlDotNet.Core.Tokens.VersionDirective;

namespace YamlDotNet.Core
{
	/// <summary>
	/// Emits YAML streams.
	/// </summary>
	public class Emitter : IEmitter
	{
		private readonly TextWriter output;

		private readonly bool isCanonical;
		private readonly int bestIndent;
		private readonly int bestWidth;
		private EmitterState state;

		private readonly Stack<EmitterState> states = new Stack<EmitterState>();
		private readonly Queue<ParsingEvent> events = new Queue<ParsingEvent>();
		private readonly Stack<int> indents = new Stack<int>();
		private readonly TagDirectiveCollection tagDirectives = new TagDirectiveCollection();
		private int indent;
		private int flowLevel;
		private bool isMappingContext;
		private bool isSimpleKeyContext;
		private bool isRootContext;

		private int line;
		private int column;
		private bool isWhitespace;
		private bool isIndentation;

		private bool isOpenEnded;

		private struct AnchorData
		{
			public string anchor;
			public bool isAlias;
		}

		private AnchorData anchorData;

		private struct TagData
		{
			public string handle;
			public string suffix;
		}

		private TagData tagData;

		private struct ScalarData
		{
			public string value;
			public bool isMultiline;
			public bool isFlowPlainAllowed;
			public bool isBlockPlainAllowed;
			public bool isSingleQuotedAllowed;
			public bool isBlockAllowed;
			public ScalarStyle style;
		}

		private bool IsUnicode
		{
			get
			{
				return
					output.Encoding == Encoding.UTF8 ||
					output.Encoding == Encoding.Unicode ||
					output.Encoding == Encoding.BigEndianUnicode ||
					output.Encoding == Encoding.UTF7 ||
					output.Encoding == Encoding.UTF32;
			}
		}

		private ScalarData scalarData;

		private const int MinBestIndent = 2;
		private const int MaxBestIndent = 9;

		/// <summary>
		/// Initializes a new instance of the <see cref="Emitter"/> class.
		/// </summary>
		/// <param name="output">The <see cref="TextWriter"/> where the emitter will write.</param>
		public Emitter(TextWriter output)
			: this(output, MinBestIndent)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="Emitter"/> class.
		/// </summary>
		/// <param name="output">The <see cref="TextWriter"/> where the emitter will write.</param>
		/// <param name="bestIndent">The preferred indentation.</param>
		public Emitter(TextWriter output, int bestIndent)
			: this(output, bestIndent, int.MaxValue)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="Emitter"/> class.
		/// </summary>
		/// <param name="output">The <see cref="TextWriter"/> where the emitter will write.</param>
		/// <param name="bestIndent">The preferred indentation.</param>
		/// <param name="bestWidth">The preferred text width.</param>
		public Emitter(TextWriter output, int bestIndent, int bestWidth)
			: this(output, bestIndent, bestWidth, false)
		{
		}


		/// <summary>
		/// Initializes a new instance of the <see cref="Emitter"/> class.
		/// </summary>
		/// <param name="output">The <see cref="TextWriter"/> where the emitter will write.</param>
		/// <param name="bestIndent">The preferred indentation.</param>
		/// <param name="bestWidth">The preferred text width.</param>
		/// <param name="isCanonical">If true, write the output in canonical form.</param>
		public Emitter(TextWriter output, int bestIndent, int bestWidth, bool isCanonical)
		{
			if (bestIndent < MinBestIndent || bestIndent > MaxBestIndent)
			{
				throw new ArgumentOutOfRangeException("bestIndent", string.Format(CultureInfo.InvariantCulture, "The bestIndent parameter must be between {0} and {1}.", MinBestIndent, MaxBestIndent));
			}

			this.bestIndent = bestIndent;

			if (bestWidth <= bestIndent * 2)
			{
				throw new ArgumentOutOfRangeException("bestWidth", "The bestWidth parameter must be greater than bestIndent * 2.");
			}

			this.bestWidth = bestWidth;

			this.isCanonical = isCanonical;

			this.output = output;
		}

		private void Write(char value)
		{
			output.Write(value);
			++column;
		}

		private void Write(string value)
		{
			output.Write(value);
			column += value.Length;
		}

		private void WriteBreak()
		{
			output.WriteLine();
			column = 0;
			++line;
		}

		/// <summary>
		/// Emit an evt.
		/// </summary>
		public void Emit(ParsingEvent @event)
		{
			events.Enqueue(@event);

			while (!NeedMoreEvents())
			{
				ParsingEvent current = events.Peek();
				AnalyzeEvent(current);
				StateMachine(current);

				// Only dequeue after calling state_machine because it checks how many events are in the queue.
				events.Dequeue();
			}
		}

		private static EventType GetEventType(ParsingEvent @event)
		{
			if (@event is AnchorAlias)
			{
				return EventType.Alias;
			}

			if (@event is DocumentEnd)
			{
				return EventType.DocumentEnd;
			}

			if (@event is DocumentStart)
			{
				return EventType.DocumentStart;
			}

			if (@event is MappingEnd)
			{
				return EventType.MappingEnd;
			}

			if (@event is MappingStart)
			{
				return EventType.MappingStart;
			}

			if (@event is Scalar)
			{
				return EventType.Scalar;
			}

			if (@event is SequenceEnd)
			{
				return EventType.SequenceEnd;
			}

			if (@event is SequenceStart)
			{
				return EventType.SequenceStart;
			}

			if (@event is StreamEnd)
			{
				return EventType.StreamEnd;
			}

			if (@event is StreamStart)
			{
				return EventType.StreamStart;
			}

			throw new ArgumentException("The specified event is of the wrong type.");
		}

		/// <summary>
		/// Check if we need to accumulate more events before emitting.
		/// 
		/// We accumulate extra
		///  - 1 event for DOCUMENT-START
		///  - 2 events for SEQUENCE-START
		///  - 3 events for MAPPING-START
		/// </summary>
		private bool NeedMoreEvents()
		{
			if (events.Count == 0)
			{
				return true;
			}

			int accumulate;
			switch (GetEventType(events.Peek()))
			{
				case EventType.DocumentStart:
					accumulate = 1;
					break;

				case EventType.SequenceStart:
					accumulate = 2;
					break;

				case EventType.MappingStart:
					accumulate = 3;
					break;

				default:
					return false;
			}

			if (events.Count > accumulate)
			{
				return false;
			}

			int level = 0;
			foreach (var evt in events)
			{
				switch (GetEventType(evt))
				{
					case EventType.DocumentStart:
					case EventType.SequenceStart:
					case EventType.MappingStart:
						++level;
						break;

					case EventType.DocumentEnd:
					case EventType.SequenceEnd:
					case EventType.MappingEnd:
						--level;
						break;
				}
				if (level == 0)
				{
					return false;
				}
			}

			return true;
		}

		private void AnalyzeAnchor(string anchor, bool isAlias)
		{
			anchorData.anchor = anchor;
			anchorData.isAlias = isAlias;
		}

		/// <summary>
		/// Check if the evt data is valid.
		/// </summary>
		private void AnalyzeEvent(ParsingEvent evt)
		{
			anchorData.anchor = null;
			tagData.handle = null;
			tagData.suffix = null;

			AnchorAlias alias = evt as AnchorAlias;
			if (alias != null)
			{
				AnalyzeAnchor(alias.Value, true);
				return;
			}

			NodeEvent nodeEvent = evt as NodeEvent;
			if (nodeEvent != null)
			{
				Scalar scalar = evt as Scalar;
				if (scalar != null)
				{
					AnalyzeScalar(scalar.Value);
				}

				AnalyzeAnchor(nodeEvent.Anchor, false);

				if (!string.IsNullOrEmpty(nodeEvent.Tag) && (isCanonical || nodeEvent.IsCanonical))
				{
					AnalyzeTag(nodeEvent.Tag);
				}
				return;
			}
		}

		/// <summary>
		/// Check if a scalar is valid.
		/// </summary>
		private void AnalyzeScalar(string value)
		{
			bool block_indicators = false;
			bool flow_indicators = false;
			bool line_breaks = false;
			bool special_characters = false;

			bool leading_space = false;
			bool leading_break = false;
			bool trailing_space = false;
			bool trailing_break = false;
			bool break_space = false;
			bool space_break = false;

			bool previous_space = false;
			bool previous_break = false;

			scalarData.value = value;

			if (value.Length == 0)
			{
				scalarData.isMultiline = false;
				scalarData.isFlowPlainAllowed = false;
				scalarData.isBlockPlainAllowed = true;
				scalarData.isSingleQuotedAllowed = true;
				scalarData.isBlockAllowed = false;
				return;
			}

			if (value.StartsWith("---", StringComparison.Ordinal) || value.StartsWith("...", StringComparison.Ordinal))
			{
				block_indicators = true;
				flow_indicators = true;
			}

			bool preceeded_by_whitespace = true;

			CharacterAnalyzer<StringLookAheadBuffer> buffer = new CharacterAnalyzer<StringLookAheadBuffer>(new StringLookAheadBuffer(value));
			bool followed_by_whitespace = buffer.IsBlankOrBreakOrZero(1);

			bool isFirst = true;
			while (!buffer.EndOfInput)
			{
				if (isFirst)
				{
					if (buffer.Check(@"#,[]{}&*!|>\""%@`"))
					{
						flow_indicators = true;
						block_indicators = true;
					}

					if (buffer.Check("?:"))
					{
						flow_indicators = true;
						if (followed_by_whitespace)
						{
							block_indicators = true;
						}
					}

					if (buffer.Check('-') && followed_by_whitespace)
					{
						flow_indicators = true;
						block_indicators = true;
					}
				}
				else
				{
					if (buffer.Check(",?[]{}"))
					{
						flow_indicators = true;
					}

					if (buffer.Check(':'))
					{
						flow_indicators = true;
						if (followed_by_whitespace)
						{
							block_indicators = true;
						}
					}

					if (buffer.Check('#') && preceeded_by_whitespace)
					{
						flow_indicators = true;
						block_indicators = true;
					}
				}


				if (!buffer.IsPrintable() || (!buffer.IsAscii() && !IsUnicode))
				{
					special_characters = true;
				}

				if (buffer.IsBreak())
				{
					line_breaks = true;
				}

				if (buffer.IsSpace())
				{
					if(isFirst)
					{
						leading_space = true;
					}
					if(buffer.Buffer.Position >= buffer.Buffer.Length - 1)
					{
						trailing_space = true;
					}
					if(previous_break) {
						break_space = true;
					}
					
					previous_space = true;
					previous_break = false;
				}

				else if (buffer.IsBreak())
				{
					if(isFirst)
					{
						leading_break = true;
					}
					if(buffer.Buffer.Position >= buffer.Buffer.Length - 1)
					{
						trailing_break = true;
					}
					
					if (previous_space)
					{
						space_break = true;
					}
					previous_space = false;
					previous_break = true;
				}
				else
				{
					previous_space = false;
					previous_break = false;
				}

				preceeded_by_whitespace = buffer.IsBlankOrBreakOrZero();
				buffer.Skip(1);
				if (!buffer.EndOfInput)
				{
					followed_by_whitespace = buffer.IsBlankOrBreakOrZero(1);
				}
				isFirst = false;
			}

			scalarData.isMultiline = line_breaks;

			scalarData.isFlowPlainAllowed = true;
			scalarData.isBlockPlainAllowed = true;
			scalarData.isSingleQuotedAllowed = true;
			scalarData.isBlockAllowed = true;

			if (leading_space || leading_break || trailing_space || trailing_break)
			{
				scalarData.isFlowPlainAllowed = false;
				scalarData.isBlockPlainAllowed = false;
			}

			if(trailing_space)
			{
				scalarData.isBlockAllowed = false;
			}
			
			if (break_space)
			{
				scalarData.isFlowPlainAllowed = false;
				scalarData.isBlockPlainAllowed = false;
				scalarData.isSingleQuotedAllowed = false;
			}

			if (space_break || special_characters)
			{
				scalarData.isFlowPlainAllowed = false;
				scalarData.isBlockPlainAllowed = false;
				scalarData.isSingleQuotedAllowed = false;
				scalarData.isBlockAllowed = false;
			}

			if (line_breaks)
			{
				scalarData.isFlowPlainAllowed = false;
				scalarData.isBlockPlainAllowed = false;
			}

			if (flow_indicators)
			{
				scalarData.isFlowPlainAllowed = false;
			}

			if (block_indicators)
			{
				scalarData.isBlockPlainAllowed = false;
			}
		}

		/// <summary>
		/// Check if a tag is valid.
		/// </summary>
		private void AnalyzeTag(string tag)
		{
			tagData.handle = tag;
			foreach (var tagDirective in tagDirectives)
			{
				if (tag.StartsWith(tagDirective.Prefix, StringComparison.Ordinal))
				{
					tagData.handle = tagDirective.Handle;
					tagData.suffix = tag.Substring(tagDirective.Prefix.Length);
					break;
				}
			}
		}

		/// <summary>
		/// State dispatcher.
		/// </summary>
		private void StateMachine(ParsingEvent evt)
		{
			switch (state)
			{
				case EmitterState.StreamStart:
					EmitStreamStart(evt);
					break;

				case EmitterState.FirstDocumentStart:
					EmitDocumentStart(evt, true);
					break;

				case EmitterState.DocumentStart:
					EmitDocumentStart(evt, false);
					break;

				case EmitterState.DocumentContent:
					EmitDocumentContent(evt);
					break;

				case EmitterState.DocumentEnd:
					EmitDocumentEnd(evt);
					break;

				case EmitterState.FlowSequenceFirstItem:
					EmitFlowSequenceItem(evt, true);
					break;

				case EmitterState.FlowSequenceItem:
					EmitFlowSequenceItem(evt, false);
					break;

				case EmitterState.FlowMappingFirstKey:
					EmitFlowMappingKey(evt, true);
					break;

				case EmitterState.FlowMappingKey:
					EmitFlowMappingKey(evt, false);
					break;

				case EmitterState.FlowMappingSimpleValue:
					EmitFlowMappingValue(evt, true);
					break;

				case EmitterState.FlowMappingValue:
					EmitFlowMappingValue(evt, false);
					break;

				case EmitterState.BlockSequenceFirstItem:
					EmitBlockSequenceItem(evt, true);
					break;

				case EmitterState.BlockSequenceItem:
					EmitBlockSequenceItem(evt, false);
					break;

				case EmitterState.BlockMappingFirstKey:
					EmitBlockMappingKey(evt, true);
					break;

				case EmitterState.BlockMappingKey:
					EmitBlockMappingKey(evt, false);
					break;

				case EmitterState.BlockMappingSimpleValue:
					EmitBlockMappingValue(evt, true);
					break;

				case EmitterState.BlockMappingValue:
					EmitBlockMappingValue(evt, false);
					break;

				case EmitterState.StreamEnd:
					throw new YamlException("Expected nothing after STREAM-END");

				default:
					Debug.Assert(false, "Invalid state.");
					throw new InvalidOperationException("Invalid state");
			}
		}

		/// <summary>
		/// Expect STREAM-START.
		/// </summary>
		private void EmitStreamStart(ParsingEvent evt)
		{
			if (!(evt is StreamStart))
			{
				throw new ArgumentException("Expected STREAM-START.", "evt");
			}

			indent = -1;
			line = 0;
			column = 0;
			isWhitespace = true;
			isIndentation = true;

			state = EmitterState.FirstDocumentStart;
		}

		/// <summary>
		/// Expect DOCUMENT-START or STREAM-END.
		/// </summary>
		private void EmitDocumentStart(ParsingEvent evt, bool isFirst)
		{
			DocumentStart documentStart = evt as DocumentStart;
			if (documentStart != null)
			{
				bool isImplicit = documentStart.IsImplicit && isFirst && !isCanonical;


				if (documentStart.Version != null && isOpenEnded)
				{
					WriteIndicator("...", true, false, false);
					WriteIndent();
				}

				if (documentStart.Version != null)
				{
					AnalyzeVersionDirective(documentStart.Version);

					isImplicit = false;
					WriteIndicator("%YAML", true, false, false);
					WriteIndicator(string.Format(CultureInfo.InvariantCulture, "{0}.{1}", Constants.MajorVersion, Constants.MinorVersion), true, false, false);
					WriteIndent();
				}

				if (documentStart.Tags != null)
				{
					foreach (var tagDirective in documentStart.Tags)
					{
						AppendTagDirective(tagDirective, false);
					}
				}

				foreach (var tagDirective in Constants.DefaultTagDirectives)
				{
					AppendTagDirective(tagDirective, true);
				}

				if (documentStart.Tags != null && documentStart.Tags.Count != 0)
				{
					isImplicit = false;
					foreach (var tagDirective in documentStart.Tags)
					{
						WriteIndicator("%TAG", true, false, false);
						WriteTagHandle(tagDirective.Handle);
						WriteTagContent(tagDirective.Prefix, true);
						WriteIndent();
					}
				}

				if (CheckEmptyDocument())
				{
					isImplicit = false;
				}

				if (!isImplicit)
				{
					WriteIndent();
					WriteIndicator("---", true, false, false);
					if (isCanonical)
					{
						WriteIndent();
					}
				}

				state = EmitterState.DocumentContent;
			}

			else if (evt is StreamEnd)
			{
				if (isOpenEnded)
				{
					WriteIndicator("...", true, false, false);
					WriteIndent();
				}

				state = EmitterState.StreamEnd;
			}
			else
			{
				throw new YamlException("Expected DOCUMENT-START or STREAM-END");
			}
		}

		/// <summary>
		/// Check if the document content is an empty scalar.
		/// </summary>
		private bool CheckEmptyDocument()
		{
			int index = 0;
			foreach (var parsingEvent in events)
			{
				if (++index == 2)
				{
					Scalar scalar = parsingEvent as Scalar;
					if (scalar != null)
					{
						return string.IsNullOrEmpty(scalar.Value);
					}
					break;
				}
			}

			return false;
		}

		private void WriteTagHandle(string value)
		{
			if (!isWhitespace)
			{
				Write(' ');
			}

			Write(value);

			isWhitespace = false;
			isIndentation = false;
		}

		private static readonly Regex uriReplacer = new Regex(@"[^0-9A-Za-z_\-;?@=$~\\\)\]/:&+,\.\*\(\[!]", RegexOptions.Compiled | RegexOptions.Singleline);

		private static string UrlEncode(string text)
		{
			return uriReplacer.Replace(text, delegate(Match match)
			{
				StringBuilder buffer = new StringBuilder();
				foreach (var toEncode in Encoding.UTF8.GetBytes(match.Value))
				{
					buffer.AppendFormat("%{0:X02}", toEncode);
				}
				return buffer.ToString();
			});
		}

		private void WriteTagContent(string value, bool needsWhitespace)
		{
			if (needsWhitespace && !isWhitespace)
			{
				Write(' ');
			}

			Write(UrlEncode(value));

			isWhitespace = false;
			isIndentation = false;
		}

		/// <summary>
		/// Append a directive to the directives stack.
		/// </summary>
		private void AppendTagDirective(TagDirective value, bool allowDuplicates)
		{
			if (tagDirectives.Contains(value))
			{
				if (allowDuplicates)
				{
					return;
				}
				else
				{
					throw new YamlException("Duplicate %TAG directive.");
				}
			}
			else
			{
				tagDirectives.Add(value);
			}
		}

		/// <summary>
		/// Check if a %YAML directive is valid.
		/// </summary>
		private static void AnalyzeVersionDirective(VersionDirective versionDirective)
		{
			if (versionDirective.Version.Major != Constants.MajorVersion || versionDirective.Version.Minor != Constants.MinorVersion)
			{
				throw new YamlException("Incompatible %YAML directive");
			}
		}

		private void WriteIndicator(string indicator, bool needWhitespace, bool whitespace, bool indentation)
		{
			if (needWhitespace && !isWhitespace)
			{
				Write(' ');
			}

			Write(indicator);

			isWhitespace = whitespace;
			isIndentation &= indentation;
			isOpenEnded = false;
		}

		private void WriteIndent()
		{
			int currentIndent = Math.Max(indent, 0);

			if (!isIndentation || column > currentIndent || (column == currentIndent && !isWhitespace))
			{
				WriteBreak();
			}

			while (column < currentIndent)
			{
				Write(' ');
			}

			isWhitespace = true;
			isIndentation = true;
		}

		/// <summary>
		/// Expect the root node.
		/// </summary>
		private void EmitDocumentContent(ParsingEvent evt)
		{
			states.Push(EmitterState.DocumentEnd);
			EmitNode(evt, true, false, false);
		}

		/// <summary>
		/// Expect a node.
		/// </summary>
		private void EmitNode(ParsingEvent evt, bool isRoot, bool isMapping, bool isSimpleKey)
		{
			isRootContext = isRoot;
			isMappingContext = isMapping;
			isSimpleKeyContext = isSimpleKey;

			var eventType = GetEventType(evt);
			switch (eventType)
			{
				case EventType.Alias:
					EmitAlias();
					break;

				case EventType.Scalar:
					EmitScalar(evt);
					break;

				case EventType.SequenceStart:
					EmitSequenceStart(evt);
					break;

				case EventType.MappingStart:
					EmitMappingStart(evt);
					break;

				default:
					throw new YamlException(string.Format("Expected SCALAR, SEQUENCE-START, MAPPING-START, or ALIAS, got {0}", eventType));
			}
		}

		/// <summary>
		/// Expect SEQUENCE-START.
		/// </summary>
		private void EmitSequenceStart(ParsingEvent evt)
		{
			ProcessAnchor();
			ProcessTag();

			SequenceStart sequenceStart = (SequenceStart)evt;

			if (flowLevel != 0 || isCanonical || sequenceStart.Style == SequenceStyle.Flow || CheckEmptySequence())
			{
				state = EmitterState.FlowSequenceFirstItem;
			}
			else
			{
				state = EmitterState.BlockSequenceFirstItem;
			}
		}

		/// <summary>
		/// Check if the next events represent an empty sequence.
		/// </summary>
		private bool CheckEmptySequence()
		{
			if (events.Count < 2)
			{
				return false;
			}

			FakeList<ParsingEvent> eventList = new FakeList<ParsingEvent>(events);
			return eventList[0] is SequenceStart && eventList[1] is SequenceEnd;
		}

		/// <summary>
		/// Check if the next events represent an empty mapping.
		/// </summary>
		private bool CheckEmptyMapping()
		{
			if (events.Count < 2)
			{
				return false;
			}

			FakeList<ParsingEvent> eventList = new FakeList<ParsingEvent>(events);
			return eventList[0] is MappingStart && eventList[1] is MappingEnd;
		}

		/// <summary>
		/// Write a tag.
		/// </summary>
		private void ProcessTag()
		{
			if (tagData.handle == null && tagData.suffix == null)
			{
				return;
			}

			if (tagData.handle != null)
			{
				WriteTagHandle(tagData.handle);
				if (tagData.suffix != null)
				{
					WriteTagContent(tagData.suffix, false);
				}
			}
			else
			{
				WriteIndicator("!<", true, false, false);
				WriteTagContent(tagData.suffix, false);
				WriteIndicator(">", false, false, false);
			}
		}

		/// <summary>
		/// Expect MAPPING-START.
		/// </summary>
		private void EmitMappingStart(ParsingEvent evt)
		{
			ProcessAnchor();
			ProcessTag();

			MappingStart mappingStart = (MappingStart)evt;

			if (flowLevel != 0 || isCanonical || mappingStart.Style == MappingStyle.Flow || CheckEmptyMapping())
			{
				state = EmitterState.FlowMappingFirstKey;
			}
			else
			{
				state = EmitterState.BlockMappingFirstKey;
			}
		}

		/// <summary>
		/// Expect SCALAR.
		/// </summary>
		private void EmitScalar(ParsingEvent evt)
		{
			SelectScalarStyle(evt);
			ProcessAnchor();
			ProcessTag();
			IncreaseIndent(true, false);
			ProcessScalar();

			indent = indents.Pop();
			state = states.Pop();
		}

		/// <summary>
		/// Write a scalar.
		/// </summary>
		private void ProcessScalar()
		{
			switch (scalarData.style)
			{
				case ScalarStyle.Plain:
					WritePlainScalar(scalarData.value, !isSimpleKeyContext);
					break;

				case ScalarStyle.SingleQuoted:
					WriteSingleQuotedScalar(scalarData.value, !isSimpleKeyContext);
					break;

				case ScalarStyle.DoubleQuoted:
					WriteDoubleQuotedScalar(scalarData.value, !isSimpleKeyContext);
					break;

				case ScalarStyle.Literal:
					WriteLiteralScalar(scalarData.value);
					break;

				case ScalarStyle.Folded:
					WriteFoldedScalar(scalarData.value);
					break;

				default:
					// Impossible.
					throw new InvalidOperationException();
			}
		}

		private static bool IsBreak(char character)
		{
			return character == '\r' || character == '\n' || character == '\x85' || character == '\x2028' || character == '\x2029';
		}

		private static bool IsBlank(char character)
		{
			return character == ' ' || character == '\t';
		}

		/// <summary>
		/// Check if the specified character is a space.
		/// </summary>
		private static bool IsSpace(char character)
		{
			return character == ' ';
		}



		private static bool IsPrintable(char character)
		{
			return
				character == '\x9' ||
				character == '\xA' ||
				character == '\xD' ||
				(character >= '\x20' && character <= '\x7E') ||
				character == '\x85' ||
				(character >= '\xA0' && character <= '\xD7FF') ||
				(character >= '\xE000' && character <= '\xFFFD');
		}

		private void WriteFoldedScalar(string value)
		{
			bool previous_break = true;
			bool leading_spaces = true;

			WriteIndicator(">", true, false, false);
			WriteBlockScalarHints(value);
			WriteBreak();

			isIndentation = true;
			isWhitespace = true;

			for (int i = 0; i < value.Length; ++i)
			{
				char character = value[i];
				if (IsBreak(character))
				{
					if (!previous_break && !leading_spaces && character == '\n')
					{
						int k = 0;
						while (i + k < value.Length && IsBreak(value[i + k]))
						{
							++k;
						}
						if (i + k < value.Length && !(IsBlank(value[i + k]) || IsBreak(value[i + k])))
						{
							WriteBreak();
						}
					}
					WriteBreak();
					isIndentation = true;
					previous_break = true;
				}
				else
				{
					if (previous_break)
					{
						WriteIndent();
						leading_spaces = IsBlank(character);
					}
					if (!previous_break && character == ' ' && i + 1 < value.Length && value[i + 1] != ' ' && column > bestWidth)
					{
						WriteIndent();
					}
					else
					{
						Write(character);
					}
					isIndentation = false;
					previous_break = false;
				}
			}
		}

		private void WriteLiteralScalar(string value)
		{
			bool previous_break = true;

			WriteIndicator("|", true, false, false);
			WriteBlockScalarHints(value);
			WriteBreak();

			isIndentation = true;
			isWhitespace = true;

			foreach (var character in value)
			{
				if (IsBreak(character))
				{
					WriteBreak();
					isIndentation = true;
					previous_break = true;
				}
				else
				{
					if (previous_break)
					{
						WriteIndent();
					}
					Write(character);
					isIndentation = false;
					previous_break = false;
				}
			}
		}

		private void WriteDoubleQuotedScalar(string value, bool allowBreaks)
		{
			WriteIndicator("\"", true, false, false);

			bool previous_space = false;
			for (int index = 0; index < value.Length; ++index)
			{
				char character = value[index];


				if (!IsPrintable(character) || IsBreak(character) || character == '"' || character == '\\')
				{
					Write('\\');

					switch (character)
					{
						case '\0':
							Write('0');
							break;

						case '\x7':
							Write('a');
							break;

						case '\x8':
							Write('b');
							break;

						case '\x9':
							Write('t');
							break;

						case '\xA':
							Write('n');
							break;

						case '\xB':
							Write('v');
							break;

						case '\xC':
							Write('f');
							break;

						case '\xD':
							Write('r');
							break;

						case '\x1B':
							Write('e');
							break;

						case '\x22':
							Write('"');
							break;

						case '\x5C':
							Write('\\');
							break;

						case '\x85':
							Write('N');
							break;

						case '\xA0':
							Write('_');
							break;

						case '\x2028':
							Write('L');
							break;

						case '\x2029':
							Write('P');
							break;

						default:
							short code = (short)character;
							if (code <= 0xFF)
							{
								Write('x');
								Write(code.ToString("X02", CultureInfo.InvariantCulture));
							}
							else
							{ //if (code <= 0xFFFF) {
								Write('u');
								Write(code.ToString("X04", CultureInfo.InvariantCulture));
							}
							//else {
							//	Write('U');
							//	Write(code.ToString("X08"));
							//}
							break;
					}
					previous_space = false;
				}
				else if (character == ' ')
				{
					if (allowBreaks && !previous_space && column > bestWidth && index > 0 && index + 1 < value.Length)
					{
						WriteIndent();
						if (value[index + 1] == ' ')
						{
							Write('\\');
						}
					}
					else
					{
						Write(character);
					}
					previous_space = true;
				}
				else
				{
					Write(character);
					previous_space = false;
				}
			}

			WriteIndicator("\"", false, false, false);

			isWhitespace = false;
			isIndentation = false;
		}

		private void WriteSingleQuotedScalar(string value, bool allowBreaks)
		{
			WriteIndicator("'", true, false, false);

			bool previous_space = false;
			bool previous_break = false;

			for (int index = 0; index < value.Length; ++index)
			{
				char character = value[index];

				if (character == ' ')
				{
					if (allowBreaks && !previous_space && column > bestWidth && index != 0 && index + 1 < value.Length && value[index + 1] != ' ')
					{
						WriteIndent();
					}
					else
					{
						Write(character);
					}
					previous_space = true;
				}
				else if (IsBreak(character))
				{
					if (!previous_break && character == '\n')
					{
						WriteBreak();
					}
					WriteBreak();
					isIndentation = true;
					previous_break = true;
				}
				else
				{
					if (previous_break)
					{
						WriteIndent();
					}
					if (character == '\'')
					{
						Write(character);
					}
					Write(character);
					isIndentation = false;
					previous_space = false;
					previous_break = false;
				}
			}

			WriteIndicator("'", false, false, false);

			isWhitespace = false;
			isIndentation = false;
		}

		private void WritePlainScalar(string value, bool allowBreaks)
		{
			if (!isWhitespace)
			{
				Write(' ');
			}

			bool previous_space = false;
			bool previous_break = false;
			for (int index = 0; index < value.Length; ++index)
			{
				char character = value[index];

				if (IsSpace(character))
				{
					if (allowBreaks && !previous_space && column > bestWidth && index + 1 < value.Length && value[index + 1] != ' ')
					{
						WriteIndent();
					}
					else
					{
						Write(character);
					}
					previous_space = true;
				}
				else if (IsBreak(character))
				{
					if (!previous_break && character == '\n')
					{
						WriteBreak();
					}
					WriteBreak();
					isIndentation = true;
					previous_break = true;
				}
				else
				{
					if (previous_break)
					{
						WriteIndent();
					}
					Write(character);
					isIndentation = false;
					previous_space = false;
					previous_break = false;
				}
			}

			isWhitespace = false;
			isIndentation = false;

			if(isRootContext)
			{
				isOpenEnded = true;
			}
		}

		/// <summary>
		/// Increase the indentation level.
		/// </summary>
		private void IncreaseIndent(bool isFlow, bool isIndentless)
		{
			indents.Push(indent);

			if (indent < 0)
			{
				indent = isFlow ? bestIndent : 0;
			}
			else if (!isIndentless)
			{
				indent += bestIndent;
			}
		}

		/// <summary>
		/// Determine an acceptable scalar style.
		/// </summary>
		private void SelectScalarStyle(ParsingEvent evt)
		{
			Scalar scalar = (Scalar)evt;

			ScalarStyle style = scalar.Style;
			bool noTag = tagData.handle == null && tagData.suffix == null;

			if (noTag && !scalar.IsPlainImplicit && !scalar.IsQuotedImplicit)
			{
				throw new YamlException("Neither tag nor isImplicit flags are specified.");
			}

			if (style == ScalarStyle.Any)
			{
				style = scalarData.isMultiline ? ScalarStyle.Folded : ScalarStyle.Plain;
			}

			if (isCanonical)
			{
				style = ScalarStyle.DoubleQuoted;
			}

			if (isSimpleKeyContext && scalarData.isMultiline)
			{
				style = ScalarStyle.DoubleQuoted;
			}

			if (style == ScalarStyle.Plain)
			{
				if ((flowLevel != 0 && !scalarData.isFlowPlainAllowed) || (flowLevel == 0 && !scalarData.isBlockPlainAllowed))
				{
					style = ScalarStyle.SingleQuoted;
				}
				if (string.IsNullOrEmpty(scalarData.value) && (flowLevel != 0 || isSimpleKeyContext))
				{
					style = ScalarStyle.SingleQuoted;
				}
				if (noTag && !scalar.IsPlainImplicit)
				{
					style = ScalarStyle.SingleQuoted;
				}
			}

			if (style == ScalarStyle.SingleQuoted)
			{
				if (!scalarData.isSingleQuotedAllowed)
				{
					style = ScalarStyle.DoubleQuoted;
				}
			}

			if (style == ScalarStyle.Literal || style == ScalarStyle.Folded)
			{
				if (!scalarData.isBlockAllowed || flowLevel != 0 || isSimpleKeyContext)
				{
					style = ScalarStyle.DoubleQuoted;
				}
			}

			// TODO: What is this code supposed to mean?
			//if (noTag && !scalar.IsQuotedImplicit && style != ScalarStyle.Plain)
			//{
			//    tagData.handle = "!";
			//}

			scalarData.style = style;
		}

		/// <summary>
		/// Expect ALIAS.
		/// </summary>
		private void EmitAlias()
		{
			ProcessAnchor();
			state = states.Pop();
		}

		/// <summary>
		/// Write an achor.
		/// </summary>
		private void ProcessAnchor()
		{
			if (anchorData.anchor != null)
			{
				WriteIndicator(anchorData.isAlias ? "*" : "&", true, false, false);
				WriteAnchor(anchorData.anchor);
			}
		}

		private void WriteAnchor(string value)
		{
			Write(value);

			isWhitespace = false;
			isIndentation = false;
		}

		/// <summary>
		/// Expect DOCUMENT-END.
		/// </summary>
		private void EmitDocumentEnd(ParsingEvent evt)
		{
			DocumentEnd documentEnd = evt as DocumentEnd;
			if (documentEnd != null)
			{
				WriteIndent();
				if (!documentEnd.IsImplicit)
				{
					WriteIndicator("...", true, false, false);
					WriteIndent();
				}

				state = EmitterState.DocumentStart;

				tagDirectives.Clear();
			}
			else
			{
				throw new YamlException("Expected DOCUMENT-END.");
			}
		}

		/// <summary>
		/// 
		/// Expect a flow item node.
		/// </summary>

		private void EmitFlowSequenceItem(ParsingEvent evt, bool isFirst)
		{
			if (isFirst)
			{
				WriteIndicator("[", true, true, false);
				IncreaseIndent(true, false);
				++flowLevel;
			}

			if (evt is SequenceEnd)
			{
				--flowLevel;
				indent = indents.Pop();
				if (isCanonical && !isFirst)
				{
					WriteIndicator(",", false, false, false);
					WriteIndent();
				}
				WriteIndicator("]", false, false, false);
				state = states.Pop();
				return;
			}

			if (!isFirst)
			{
				WriteIndicator(",", false, false, false);
			}

			if (isCanonical || column > bestWidth)
			{
				WriteIndent();
			}

			states.Push(EmitterState.FlowSequenceItem);

			EmitNode(evt, false, false, false);
		}

		/// <summary>
		/// Expect a flow key node.
		/// </summary>
		private void EmitFlowMappingKey(ParsingEvent evt, bool isFirst)
		{
			if (isFirst)
			{
				WriteIndicator("{", true, true, false);
				IncreaseIndent(true, false);
				++flowLevel;
			}

			if (evt is MappingEnd)
			{
				--flowLevel;
				indent = indents.Pop();
				if (isCanonical && !isFirst)
				{
					WriteIndicator(",", false, false, false);
					WriteIndent();
				}
				WriteIndicator("}", false, false, false);
				state = states.Pop();
				return;
			}

			if (!isFirst)
			{
				WriteIndicator(",", false, false, false);
			}
			if (isCanonical || column > bestWidth)
			{
				WriteIndent();
			}

			if (!isCanonical && CheckSimpleKey())
			{
				states.Push(EmitterState.FlowMappingSimpleValue);
				EmitNode(evt, false, true, true);
			}
			else
			{
				WriteIndicator("?", true, false, false);
				states.Push(EmitterState.FlowMappingValue);
				EmitNode(evt, false, true, false);
			}
		}

		private const int MaxAliasLength = 128;

		private static int SafeStringLength(string value)
		{
			return value != null ? value.Length : 0;
		}

		/// <summary>
		/// Check if the next node can be expressed as a simple key.
		/// </summary>
		private bool CheckSimpleKey()
		{
			if (events.Count < 1)
			{
				return false;
			}

			int length;
			switch (GetEventType(events.Peek()))
			{
				case EventType.Alias:
					length = SafeStringLength(anchorData.anchor);
					break;

				case EventType.Scalar:
					if (scalarData.isMultiline)
					{
						return false;
					}

					length =
						SafeStringLength(anchorData.anchor) +
						SafeStringLength(tagData.handle) +
						SafeStringLength(tagData.suffix) +
						SafeStringLength(scalarData.value);
					break;

				case EventType.SequenceStart:
					if (!CheckEmptySequence())
					{
						return false;
					}
					length =
						SafeStringLength(anchorData.anchor) +
						SafeStringLength(tagData.handle) +
						SafeStringLength(tagData.suffix);
					break;

				case EventType.MappingStart:
					if (!CheckEmptySequence())
					{
						return false;
					}
					length =
						SafeStringLength(anchorData.anchor) +
						SafeStringLength(tagData.handle) +
						SafeStringLength(tagData.suffix);
					break;

				default:
					return false;
			}

			return length <= MaxAliasLength;
		}

		/// <summary>
		/// Expect a flow value node.
		/// </summary>
		private void EmitFlowMappingValue(ParsingEvent evt, bool isSimple)
		{
			if (isSimple)
			{
				WriteIndicator(":", false, false, false);
			}
			else
			{
				if (isCanonical || column > bestWidth)
				{
					WriteIndent();
				}
				WriteIndicator(":", true, false, false);
			}
			states.Push(EmitterState.FlowMappingKey);
			EmitNode(evt, false, true, false);
		}

		/// <summary>
		/// Expect a block item node.
		/// </summary>
		private void EmitBlockSequenceItem(ParsingEvent evt, bool isFirst)
		{
			if (isFirst)
			{
				IncreaseIndent(false, (isMappingContext && !isIndentation));
			}

			if (evt is SequenceEnd)
			{
				indent = indents.Pop();
				state = states.Pop();
				return;
			}

			WriteIndent();
			WriteIndicator("-", true, false, true);
			states.Push(EmitterState.BlockSequenceItem);

			EmitNode(evt, false, false, false);
		}

		/// <summary>
		/// Expect a block key node.
		/// </summary>
		private void EmitBlockMappingKey(ParsingEvent evt, bool isFirst)
		{
			if (isFirst)
			{
				IncreaseIndent(false, false);
			}

			if (evt is MappingEnd)
			{
				indent = indents.Pop();
				state = states.Pop();
				return;
			}

			WriteIndent();

			if (CheckSimpleKey())
			{
				states.Push(EmitterState.BlockMappingSimpleValue);
				EmitNode(evt, false, true, true);
			}
			else
			{
				WriteIndicator("?", true, false, true);
				states.Push(EmitterState.BlockMappingValue);
				EmitNode(evt, false, true, false);
			}
		}

		/// <summary>
		/// Expect a block value node.
		/// </summary>
		private void EmitBlockMappingValue(ParsingEvent evt, bool isSimple)
		{
			if (isSimple)
			{
				WriteIndicator(":", false, false, false);
			}
			else
			{
				WriteIndent();
				WriteIndicator(":", true, false, true);
			}
			states.Push(EmitterState.BlockMappingKey);
			EmitNode(evt, false, true, false);
		}

		private void WriteBlockScalarHints(string value)
		{
			var analyzer = new CharacterAnalyzer<StringLookAheadBuffer>(new StringLookAheadBuffer(value));

			if(analyzer.IsSpace() || analyzer.IsBreak()) {
				string indent_hint = string.Format(CultureInfo.InvariantCulture, "{0}\0", bestIndent);
				WriteIndicator(indent_hint, false, false, false);
			}

			isOpenEnded = false;

			string chomp_hint = null;
			if(value.Length == 0 || !analyzer.IsBreak(value.Length - 1)) {
				chomp_hint = "-";
			}
			else if(value.Length >= 2 && analyzer.IsBreak(value.Length - 2)) {
				chomp_hint = "+";
				isOpenEnded = true;
			}

			if(chomp_hint != null){
				WriteIndicator(chomp_hint, false, false, false);
			}
		}
	}
}
