using System;
using System.Linq;
using GlazeWM.Domain.Common.Enums;
using GlazeWM.Domain.Containers;
using GlazeWM.Domain.Containers.Commands;
using GlazeWM.Domain.Containers.Events;
using GlazeWM.Domain.Monitors;
using GlazeWM.Domain.UserConfigs;
using GlazeWM.Domain.Windows.Commands;
using GlazeWM.Domain.Workspaces;
using GlazeWM.Infrastructure.Bussing;
using GlazeWM.Infrastructure.Utils;
using GlazeWM.Infrastructure.WindowsApi;

namespace GlazeWM.Domain.Windows.CommandHandlers
{
  internal sealed class MoveWindowHandler : ICommandHandler<MoveWindowCommand>
  {
    private readonly Bus _bus;
    private readonly ContainerService _containerService;
    private readonly MonitorService _monitorService;
    private readonly UserConfigService _userConfigService;

    public MoveWindowHandler(
      Bus bus,
      ContainerService containerService,
      MonitorService monitorService,
      UserConfigService userConfigService)
    {
      _bus = bus;
      _containerService = containerService;
      _monitorService = monitorService;
      _userConfigService = userConfigService;
    }

    public CommandResponse Handle(MoveWindowCommand command)
    {
      var windowToMove = command.WindowToMove;
      var direction = command.Direction;

      return windowToMove switch
      {
        FloatingWindow window => MoveFloatingWindow(window, direction),
        TilingWindow window => MoveTilingWindow(window, direction),
        _ => throw new Exception("Cannot move given window type. This is a bug."),
      };
    }

    private CommandResponse MoveTilingWindow(
      TilingWindow windowToMove,
      Direction direction)
    {
      var layoutForDirection = direction.GetCorrespondingLayout();
      var parentMatchesLayout =
        (windowToMove.Parent as SplitContainer).Layout ==
        direction.GetCorrespondingLayout();

      var hasResizableSiblings = windowToMove.SiblingsOfType<IResizable>().Any();

      // Attempt to the move window to workspace in given direction.
      if (
        windowToMove.Parent is Workspace &&
        (!hasResizableSiblings || parentMatchesLayout) &&
        !HasSiblingInDirection(windowToMove, direction))
      {
        MoveToWorkspaceInDirection(windowToMove, direction);
        return CommandResponse.Ok;
      }

      // Find an ancestor that the window can be moved to.
      var ancestorWithLayout = windowToMove.Ancestors.FirstOrDefault(
        container => (container as SplitContainer)?.Layout == layoutForDirection
      );

      // If there is no suitable ancestor, then change the layout of the workspace.
      if (ancestorWithLayout is null)
      {
        ancestorWithLayout ??= ChangeWorkspaceLayout(windowToMove, layoutForDirection);
        parentMatchesLayout = true;
      }

      if (parentMatchesLayout && HasSiblingInDirection(windowToMove, direction))
      {
        SwapSiblingContainers(windowToMove, direction);
        return CommandResponse.Ok;
      }

      // Move the container into the given ancestor. This could simply be the container's
      // direct parent.
      MoveIntoAncestor(windowToMove, direction, ancestorWithLayout);

      return CommandResponse.Ok;
    }

    /// <summary>
    /// Whether the window has a tiling sibling in the given direction.
    /// </summary>
    private static bool HasSiblingInDirection(Window windowToMove, Direction direction)
    {
      if (direction is Direction.Up or Direction.Left)
        return windowToMove != windowToMove.SelfAndSiblingsOfType<IResizable>().First();

      return windowToMove != windowToMove.SelfAndSiblingsOfType<IResizable>().Last();
    }

    private void SwapSiblingContainers(Window windowToMove, Direction direction)
    {
      var siblingInDirection = direction is Direction.Up or Direction.Left
        ? windowToMove.PreviousSiblingOfType<IResizable>()
        : windowToMove.NextSiblingOfType<IResizable>();

      // Swap the window with sibling in given direction.
      if (siblingInDirection is Window)
      {
        var insertIndex = direction is Direction.Up or Direction.Left
          ? siblingInDirection.Index
          : siblingInDirection.Index + 1;

        _bus.Invoke(
          new MoveContainerWithinTreeCommand(
            windowToMove,
            windowToMove.Parent,
            insertIndex,
            false
          )
        );

        _bus.Invoke(new RedrawContainersCommand());
        return;
      }

      // Move the window into the sibling split container.
      var targetDescendant = _containerService.GetDescendantInDirection(
        siblingInDirection,
        direction.Inverse()
      );
      var targetParent = targetDescendant.Parent as SplitContainer;

      var layoutForDirection = direction.GetCorrespondingLayout();
      var shouldInsertAfter =
        targetParent.Layout != layoutForDirection ||
        direction == Direction.Up ||
        direction == Direction.Left;

      var insertionIndex = shouldInsertAfter
        ? targetDescendant.Index + 1
        : targetDescendant.Index;

      _bus.Invoke(
        new MoveContainerWithinTreeCommand(
          windowToMove,
          targetParent,
          insertionIndex,
          true
        )
      );

      _bus.Invoke(new RedrawContainersCommand());
    }

    private void MoveToWorkspaceInDirection(Window windowToMove, Direction direction)
    {
      var monitor = windowToMove.Ancestors.OfType<Monitor>().First();
      var monitorInDirection = _monitorService.GetMonitorInDirection(direction, monitor);
      var workspaceInDirection = monitorInDirection?.DisplayedWorkspace;

      if (workspaceInDirection is null)
        return;

      // Since window is crossing monitors, adjustments might need to be made because of DPI.
      if (MonitorService.HasDpiDifference(windowToMove, workspaceInDirection))
        windowToMove.HasPendingDpiAdjustment = true;

      // Update floating placement since the window has to cross monitors.
      windowToMove.FloatingPlacement =
        windowToMove.FloatingPlacement.TranslateToCenter(workspaceInDirection.ToRect());

      // TODO: Descend into container if possible.
      if (direction is Direction.Up or Direction.Left)
        _bus.Invoke(new MoveContainerWithinTreeCommand(windowToMove, workspaceInDirection, true));
      else
        _bus.Invoke(new MoveContainerWithinTreeCommand(windowToMove, workspaceInDirection, 0, true));

      _bus.Invoke(new RedrawContainersCommand());

      // Refresh state in bar of which workspace has focus.
      _bus.Emit(new FocusChangedEvent(windowToMove));
    }

    private Workspace ChangeWorkspaceLayout(Window windowToMove, Layout layout)
    {
      var workspace = windowToMove.Ancestors.OfType<Workspace>().First();

      _bus.Invoke(new ChangeContainerLayoutCommand(workspace, layout));

      // Create a new split container to wrap siblings.
      var splitContainer = new SplitContainer
      {
        Layout = layout.Inverse(),
        SizePercentage = 0.5,
        // Children = windowToMove.Parent.Children.Where(con => con != windowToMove).ToList(),
        // ChildFocusOrder = windowToMove.Parent.ChildFocusOrder.Where(con => con != windowToMove).ToList(),
      };

      var siblings = windowToMove.Parent.ChildFocusOrder
        .Where(child => child != windowToMove && child is IResizable)
        .Reverse();

      // var resizableSiblings = siblings.OfType<IResizable>();
      var sizePercentageIncrement =
        (windowToMove as TilingWindow).SizePercentage / siblings.Count();

      // TODO: Create command `WrapInSplitContainer` (can be re-used for
      // `ChangeContainerLayoutHandler`).
      foreach (var sibling in siblings)
      {
        _bus.Invoke(new DetachContainerCommand(sibling));
        _bus.Invoke(new AttachContainerCommand(sibling, splitContainer));
        (sibling as IResizable).SizePercentage += sizePercentageIncrement;
      }

      // TODO: What to do here?
      // var insertionIndex = direction is Direction.Up or Direction.Left
      //   ? 0
      //   : 1;

      (windowToMove as IResizable).SizePercentage = 0.5;
      _bus.Invoke(new AttachContainerCommand(splitContainer, workspace));

      return workspace;
    }

    private void MoveIntoAncestor(
      TilingWindow windowToMove,
      Direction direction,
      Container ancestorWithLayout)
    {
      // Traverse up from `windowToMove` to find container where the parent is
      // `ancestorWithLayout`. Then depending on the direction, insert before or after
      // that container.
      var insertionReference = windowToMove.SelfAndAncestors
        .FirstOrDefault(container => container.Parent == ancestorWithLayout);

      var insertionReferenceSibling = direction is Direction.Up or Direction.Left
        ? insertionReference.PreviousSiblingOfType<IResizable>()
        : insertionReference.NextSiblingOfType<IResizable>();

      if (insertionReferenceSibling is not SplitContainer)
      {
        // Move the window into the container above.
        var insertIndex = direction is Direction.Up or Direction.Left
          ? insertionReference.Index
          : insertionReference.Index + 1;

        _bus.Invoke(new MoveContainerWithinTreeCommand(windowToMove, ancestorWithLayout, insertIndex, true));

        _bus.Invoke(new RedrawContainersCommand());
        return;
      }

      // Move the window into the adjacent split container.
      var targetDescendant = _containerService.GetDescendantInDirection(
        insertionReferenceSibling,
        direction.Inverse()
      );

      var targetParent = targetDescendant.Parent as SplitContainer;

      var layoutForDirection = direction.GetCorrespondingLayout();
      var shouldInsertAfter =
        targetParent.Layout != layoutForDirection ||
        direction == Direction.Up ||
        direction == Direction.Left;

      var insertionIndex = shouldInsertAfter
        ? targetDescendant.Index + 1
        : targetDescendant.Index;

      _bus.Invoke(new MoveContainerWithinTreeCommand(windowToMove, targetParent, insertionIndex, true));
      _bus.Invoke(new RedrawContainersCommand());
    }

    private CommandResponse MoveFloatingWindow(Window windowToMove, Direction direction)
    {
      // Get move amount from user config.
      var moveAmount = _userConfigService.GeneralConfig.FloatingWindowMoveAmount;

      var amount = UnitsHelper.TrimUnits(moveAmount);
      var units = UnitsHelper.GetUnits(moveAmount);
      var currentMonitor = windowToMove.Ancestors.OfType<Monitor>().First();

      amount = units switch
      {
        "%" => amount * currentMonitor.Width / 100,
        "ppt" => amount * currentMonitor.Width / 100,
        "px" => amount,
        // in case user only provided a number in the config;
        // TODO: somehow validate floating_window_move_amount in config on startup
        _ => amount
        // _ => throw new ArgumentException(null, nameof(amount)),
      };

      var x = windowToMove.FloatingPlacement.X;
      var y = windowToMove.FloatingPlacement.Y;

      _ = direction switch
      {
        Direction.Left => x -= amount,
        Direction.Right => x += amount,
        Direction.Up => y -= amount,
        Direction.Down => y += amount,
        _ => throw new ArgumentException(null, nameof(direction))
      };

      // Make sure grabbable space on top is always visible
      var monitorAbove = _monitorService.GetMonitorInDirection(Direction.Up, currentMonitor);
      if (y < currentMonitor.Y && monitorAbove is null)
        y = currentMonitor.Y;

      var newPlacement = Rect.FromXYCoordinates(x, y, windowToMove.FloatingPlacement.Width, windowToMove.FloatingPlacement.Height);
      var center = newPlacement.GetCenterPoint();

      // If new placement wants to cross monitors && direction == ... is for edge case
      // when user places window center outside a monitor with a mouse
      if (
        (center.X >= currentMonitor.Width + currentMonitor.X && direction == Direction.Right) ||
        (center.X < currentMonitor.X && direction == Direction.Left) ||
        (center.Y < currentMonitor.Y && direction == Direction.Up) ||
        (center.Y >= currentMonitor.Height + currentMonitor.Y && direction == Direction.Down))
      {
        var monitorInDirection = _monitorService.GetMonitorInDirection(direction, currentMonitor);
        var workspaceInDirection = monitorInDirection?.DisplayedWorkspace;

        if (workspaceInDirection is null)
          return CommandResponse.Ok;

        // Change the window's parent workspace.
        _bus.Invoke(new MoveContainerWithinTreeCommand(windowToMove, workspaceInDirection, false));
        _bus.Emit(new FocusChangedEvent(windowToMove));

        // Redrawing twice to fix weird WindowsOS dpi behaviour
        windowToMove.HasPendingDpiAdjustment = true;
      }

      windowToMove.FloatingPlacement = newPlacement;

      _containerService.ContainersToRedraw.Add(windowToMove);
      _bus.Invoke(new RedrawContainersCommand());

      return CommandResponse.Ok;
    }
  }
}
