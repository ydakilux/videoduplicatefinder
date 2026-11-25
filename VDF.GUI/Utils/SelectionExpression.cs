// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//     VideoDuplicateFinder is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//     You should have received a copy of the GNU Affero General Public License
//     along with VideoDuplicateFinder.  If not, see <http://www.gnu.org/licenses/>.
// */
//

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using System.Threading;
using DynamicExpresso;
using VDF.Core.ViewModels;
using RegexEngine = System.Text.RegularExpressions.Regex;

namespace VDF.GUI.Utils {
	/// <summary>Compiles the custom-selection expression ("item.Path.Contains(...)").</summary>
	internal static class SelectionExpression {
		public const string Identifier = "item";
		internal const string RegexTimeoutMessage = "Regular expression match timed out.";
		internal static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
		internal sealed class MatchTimedOutException(Exception? cause = null) : TimeoutException(RegexTimeoutMessage, cause) { }

		// Keep the expression syntax, but never register the framework Regex type:
		// its constructors and default overloads permit unbounded matching.
		public static class Regex {
			public static bool IsMatch(string input, string pattern) => IsMatch(input, pattern, RegexOptions.None);
			public static bool IsMatch(string input, string pattern, RegexOptions options) =>
				RegexEngine.IsMatch(input, pattern, options, RegexTimeout);
			public static Match Match(string input, string pattern) => Match(input, pattern, RegexOptions.None);
			public static Match Match(string input, string pattern, RegexOptions options) =>
				RegexEngine.Match(input, pattern, options, RegexTimeout);
			public static MatchCollection Matches(string input, string pattern) => Matches(input, pattern, RegexOptions.None);
			public static MatchCollection Matches(string input, string pattern, RegexOptions options) =>
				RegexEngine.Matches(input, pattern, options, RegexTimeout);
			public static string Replace(string input, string pattern, string replacement) => Replace(input, pattern, replacement, RegexOptions.None);
			public static string Replace(string input, string pattern, string replacement, RegexOptions options) =>
				RegexEngine.Replace(input, pattern, replacement, options, RegexTimeout);
			public static string[] Split(string input, string pattern) => Split(input, pattern, RegexOptions.None);
			public static string[] Split(string input, string pattern, RegexOptions options) =>
				RegexEngine.Split(input, pattern, options, RegexTimeout);
			public static string Escape(string text) => RegexEngine.Escape(text);
			public static string Unescape(string text) => RegexEngine.Unescape(text);
		}

		// Release builds are Native AOT: the trimmer drops every member nothing
		// references statically, but DynamicExpresso resolves members via reflection
		// at runtime - so without these roots even string.Contains fails with
		// "No applicable method 'Contains' exists in type 'String'" (#844). Every
		// type an expression can touch must be listed here.
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(string))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(Regex))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(Match))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(MatchCollection))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(GroupCollection))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(Group))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicFields, typeof(RegexOptions))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(Math))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(TimeSpan))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(DateTime))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(long))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(int))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(float))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(double))]
		// decimal is special: DynamicExpresso probes int->decimal coercion during overload
		// resolution, and that goes through Decimal.op_Implicit - without this root even
		// "item.Duration.Minutes > 15" fails to parse ("Invalid Operation").
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(decimal))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(Guid))]
		[DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(DuplicateItem))]
		internal static Func<DuplicateItem, bool> Compile(string expression) {
			// Use PrimitiveTypes only - avoids registering types like Convert, Activator, etc.
			// that could be abused if a malicious expression is loaded from a crafted settings file.
			// Assignments are disabled: "item.IsBestSize = true" (a typo for ==) would otherwise
			// WRITE the property on every item and match everything; now it's a parse error.
			var predicate = new Interpreter(InterpreterOptions.PrimitiveTypes | InterpreterOptions.SystemKeywords)
				.EnableAssignment(AssignmentOperators.None)
				.Reference(typeof(TimeSpan))
				.Reference(typeof(Math))
				.Reference(typeof(Regex))
				.Reference(typeof(RegexOptions))
				.ParseAsDelegate<Func<DuplicateItem, bool>>(expression, Identifier);
			// Audit callers may catch per-item errors and keep evaluating. Stop running
			// regexes after the first timeout rather than paying the timeout per item.
			bool timedOut = false;
			return item => {
				if (Volatile.Read(ref timedOut)) throw new MatchTimedOutException();
				try {
					return predicate(item);
				}
				catch (RegexMatchTimeoutException ex) {
					Volatile.Write(ref timedOut, true);
					throw new MatchTimedOutException(ex);
				}
			};
		}
	}
}
