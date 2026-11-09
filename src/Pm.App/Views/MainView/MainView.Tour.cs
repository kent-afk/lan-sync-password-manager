using Path = Avalonia.Controls.Shapes.Path;

namespace Pm.App.Views.MainView;

public sealed partial class MainView
{
    // ---------- onboarding ----------

    private void ShowTour(bool force = false)
    {
        if (_session is null) return;
        if (!force && _session.Config.TourShown) return;
        CloseTour();

        var steps = new List<(Func<Control?> Target, string Title, string Body)>
        {
            (() => _addButton, _s.Tour1Title, _s.Tour1Body),
            (() => _pairButton, _s.Tour2Title, _s.Tour2Body),
            (() => _status, _s.Tour3Title, _s.Tour3Body),
        };

        var index = 0;
        var lastAdvance = DateTime.MinValue;
        var layer = new Grid { Background = Ui.Dim };
        var canvas = new Canvas();
        layer.Children.Add(canvas);
        _root.Children.Add(layer);
        _tour = layer;

        void Render()
        {
            canvas.Children.Clear();
            var (target, title, body) = steps[index];
            var control = target();
            if (control is null)
            {
                FinishTour();
                return;
            }

            var position = control.TranslatePoint(new Point(0, 0), _root) ?? new Point(0, 0);
            var rect = new Rect(position, control.Bounds.Size);
            var width = Math.Max(_root.Bounds.Width, 480);
            var height = Math.Max(_root.Bounds.Height, 420);

            var ring = new Border
            {
                Width = rect.Width + 12,
                Height = rect.Height + 12,
                BorderBrush = Ui.Accent,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(9),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(ring, rect.X - 6);
            Canvas.SetTop(ring, rect.Y - 6);
            canvas.Children.Add(ring);

            // The last step points at the bottom status bar; keep its card over the
            // left list column so it does not cover the editor's action buttons.
            var statusStep = index == steps.Count - 1;
            var cardWidth = statusStep ? Math.Min(264, Math.Max(200, width - 32)) : 320.0;
            var left = statusStep
                ? 16
                : Math.Clamp(rect.Center.X - cardWidth / 2, 16, Math.Max(16, width - cardWidth - 16));

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            actions.Children.Add(Ui.Ghost(_s.Skip, FinishTour));
            actions.Children.Add(Ui.Button(index == steps.Count - 1 ? _s.Done : _s.Next, () =>
            {
                var now = DateTime.UtcNow;
                if ((now - lastAdvance).TotalMilliseconds < 350) return;
                lastAdvance = now;

                if (index == steps.Count - 1)
                {
                    FinishTour();
                    return;
                }

                index++;
                Render();
            }, primary: true));

            var content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextStrong },
                    new TextBlock { Text = body, FontSize = 13, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap },
                    actions,
                },
            };

            var card = Ui.Card(content, cardWidth);
            card.Measure(new Size(cardWidth, double.PositiveInfinity));
            var cardHeight = card.DesiredSize.Height;
            var safeBottom = Math.Max(16, height - 80);
            var below = rect.Bottom + 24 + cardHeight <= safeBottom;
            var top = below
                ? rect.Bottom + 24
                : Math.Clamp(rect.Top - cardHeight - 24, 16, Math.Max(16, safeBottom - cardHeight));
            card.HorizontalAlignment = HorizontalAlignment.Left;
            card.VerticalAlignment = VerticalAlignment.Top;
            card.Opacity = 0;
            var slide = new TranslateTransform(0, 8);
            card.RenderTransform = slide;
            slide.Transitions = new Transitions
            {
                new DoubleTransition { Property = TranslateTransform.YProperty, Duration = TimeSpan.FromMilliseconds(260), Easing = Ui.Ease },
            };
            card.Transitions = new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(260), Easing = Ui.Ease },
            };

            Canvas.SetLeft(card, left);
            Canvas.SetTop(card, top);
            canvas.Children.Add(card);

            var arrow = new Path
            {
                Data = Geometry.Parse(below ? "M0,10 L8,0 L16,10 Z" : "M0,0 L8,10 L16,0 Z"),
                Fill = Ui.Accent,
                Width = 16,
                Height = 10,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(arrow, statusStep ? left + cardWidth / 2 - 8 : rect.Center.X - 8);
            Canvas.SetTop(arrow, below ? rect.Bottom + 8 : rect.Top - 18);
            canvas.Children.Add(arrow);

            Dispatcher.UIThread.Post(() =>
            {
                card.Opacity = 1;
                slide.Y = 0;
            });
        }

        Render();
    }

    private void CloseTour()
    {
        if (_tour is null) return;
        _root.Children.Remove(_tour);
        _tour = null;
    }

    private void FinishTour()
    {
        CloseTour();
        if (_session is null) return;
        _session.Config.TourShown = true;
        _session.SaveConfig();
    }
}
