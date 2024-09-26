﻿
using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "These are only Lists because arrays are more annoying to use", Scope = "namespaceanddescendants", Target = "~N:Lizard.Logic.Threads")]
[assembly: SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "These aren't handled in an exception-specific way", Scope = "namespaceanddescendants", Target = "~N:Lizard.Logic")]
[assembly: SuppressMessage("Design", "CA1051:Do not declare visible instance fields", Justification = "This is a fair point but I don't care", Scope = "namespaceanddescendants", Target = "~N:Lizard.Logic")]
[assembly: SuppressMessage("Design", "CA1062:Validate arguments of public methods", Justification = "Validate them yourself, you lazy calling method", Scope = "namespaceanddescendants", Target = "~N:Lizard.Logic")]
[assembly: SuppressMessage("Design", "CA1040:Avoid empty interfaces", Scope = "namespaceanddescendants", Target = "~N:Lizard")]



[assembly: SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "These are for informational purposes only", Scope = "namespaceanddescendants", Target = "~N:Lizard")]
[assembly: SuppressMessage("Globalization", "CA1304:Specify CultureInfo", Justification = "Most strings (FEN, UCI, etc.) are in English", Scope = "namespaceanddescendants", Target = "~N:Lizard")]
[assembly: SuppressMessage("Globalization", "CA1305:Specify IFormatProvider", Justification = "Most strings (FEN, UCI, etc.) are in English", Scope = "namespaceanddescendants", Target = "~N:Lizard")]
[assembly: SuppressMessage("Globalization", "CA1307:Specify StringComparison for clarity", Justification = "Most strings (FEN, UCI, etc.) are in English", Scope = "namespaceanddescendants", Target = "~N:Lizard")]
[assembly: SuppressMessage("Globalization", "CA1311:Specify a culture or use an invariant version", Justification = "Piece names are always in English", Scope = "namespaceanddescendants", Target = "~N:Lizard")]
[assembly: SuppressMessage("Globalization", "CA1310:Specify StringComparison for correctness", Scope = "namespaceanddescendants", Target = "~N:Lizard")]



[assembly: SuppressMessage("Performance", "CA1810:Initialize reference type static fields inline", Justification = "This doesn't seem to be an issue", Scope = "namespaceanddescendants", Target = "~N:Lizard")]
[assembly: SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Scope = "namespaceanddescendants", Target = "~N:Lizard.Logic")]
[assembly: SuppressMessage("Performance", "CA1805:Do not initialize unnecessarily", Scope = "namespaceanddescendants", Target = "~N:Lizard.Logic")]



[assembly: SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Constants are more readable", Scope = "namespaceanddescendants", Target = "~N:Lizard")]



[assembly: SuppressMessage("Usage", "CA2211:Non-constant fields should not be visible", Scope = "namespaceanddescendants", Target = "~N:Lizard")]
[assembly: SuppressMessage("Usage", "CA2201:Do not raise reserved exception types", Scope = "namespaceanddescendants", Target = "~N:Lizard")]



[assembly: SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Scope = "namespaceanddescendants", Target = "~N:Lizard")]

#pragma warning disable CS8625