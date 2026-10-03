using System.Diagnostics;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Layout;

internal static class FlexValidation
{
    [Conditional("DEBUG")]
    internal static void Check(in Flex flex)
    {
        DevelopmentCheck.Require(flex.Direction is Axis.Horizontal or Axis.Vertical,
            "Flex.Direction requires Horizontal or Vertical.");
        DevelopmentCheck.Require(float.IsFinite(flex.Gap) && flex.Gap >= 0,
            "Flex.Gap requires a finite nonnegative value.");
        DevelopmentCheck.Require(flex.MainAlign is Align.Start or Align.Center or Align.End or Align.SpaceBetween,
            "Flex.MainAlign requires Start, Center, End, or SpaceBetween.");
        DevelopmentCheck.Require(flex.CrossAlign is Align.Start or Align.Center or Align.End or Align.Stretch,
            "Flex.CrossAlign requires Start, Center, End, or Stretch.");
    }

    [Conditional("DEBUG")]
    internal static void CheckItem(in FlexItem item)
    {
        DevelopmentCheck.Require(float.IsFinite(item.Grow) && item.Grow >= 0,
            "FlexItem.Grow requires a finite nonnegative weight.");
        DevelopmentCheck.Require(float.IsFinite(item.Shrink) && item.Shrink >= 0,
            "FlexItem.Shrink requires a finite nonnegative weight.");
        DevelopmentCheck.Require(float.IsNaN(item.Basis) || float.IsFinite(item.Basis) && item.Basis >= 0,
            "FlexItem.Basis requires a finite nonnegative size or NaN for automatic measurement.");
        DevelopmentCheck.Require(item.Self is Align.Auto or Align.Start or Align.Center or Align.End or Align.Stretch,
            "FlexItem.Self requires Auto, Start, Center, End, or Stretch.");
    }
}
