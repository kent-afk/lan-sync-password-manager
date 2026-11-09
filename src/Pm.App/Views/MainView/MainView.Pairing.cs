namespace Pm.App.Views.MainView;

public sealed partial class MainView
{
    // ---------- pairing ----------

    private void ShowPairing()
    {
        if (_session is null) return;
        ResetDelete();
        CloseOverlay();

        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = _s.PairTitle,
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = Ui.TextStrong,
        });

        var resultText = new TextBlock { Foreground = Ui.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap };

        if (_host.CanOfferPairing)
        {
            var offerAddress = new TextBlock { Foreground = Ui.Faint, FontSize = 12 };
            var offerCode = new TextBlock
            {
                FontFamily = new FontFamily("monospace"),
                FontSize = 30,
                FontWeight = FontWeight.SemiBold,
                Foreground = Ui.TextStrong,
                IsVisible = false,
            };
            var offerStatus = new TextBlock { Foreground = Ui.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap };

            Button? offerButton = null;
            offerButton = Ui.Button(_s.PairShow, async () =>
            {
                offerButton!.IsEnabled = false;
                var offer = await _host.OfferPairingAsync(_session);
                if (offer is null)
                {
                    offerStatus.Text = _s.PairNone;
                    offerButton!.IsEnabled = true;
                    return;
                }

                _pairOffer = offer;
                offerAddress.Text = offer.Address;
                offerCode.Text = offer.Code;
                offerCode.IsVisible = true;
                offerStatus.Text = _s.PairWaiting;

                try
                {
                    var remaining = offer.Expires - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        offerStatus.Text = _s.PairExpired;
                        return;
                    }

                    PairPending pending;
                    try
                    {
                        pending = await offer.Pending.WaitAsync(remaining);
                    }
                    catch (TimeoutException)
                    {
                        offerStatus.Text = _s.PairExpired;
                        return;
                    }

                    offerStatus.Text = _s.PairWaitingConfirm;
                    var accepted = await ConfirmFingerprintsAsync(pending.OwnFingerprint, pending.Fingerprint);
                    offer.Approve(accepted);
                    if (!accepted)
                    {
                        offerStatus.Text = _s.PairCancelled;
                        return;
                    }

                    var peer = await offer.Completed;
                    offerStatus.Text = $"{_s.PairDone}: {peer.Label}\n{_s.PairFingerprint(peer.OwnFingerprint, peer.Fingerprint)}";
                    RefreshList();
                    UpdateMeta();
                }
                catch (Exception ex)
                {
                    offerStatus.Text = ex.Message;
                }
                finally
                {
                    await offer.DisposeAsync();
                    _pairOffer = null;
                    offerButton!.IsEnabled = true;
                }
            });

            var offerStack = new StackPanel
            {
                Spacing = 6,
                Children = { offerButton, offerAddress, offerCode, offerStatus },
            };
            panel.Children.Add(offerStack);
            panel.Children.Add(new Border { Height = 1, Background = Ui.LineFaint, Margin = new Thickness(0, 6) });
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = _s.PairIosHint, FontSize = 12, Foreground = Ui.Faint, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new Border { Height = 1, Background = Ui.LineFaint, Margin = new Thickness(0, 6) });
        }

        var found = new StackPanel { Spacing = 6 };
        var address = Ui.Input(_s.PairAddress);
        var code = Ui.Input(_s.PairCode);
        code.Width = 120;

        Button? findButton = null;
        findButton = Ui.Button(_s.PairFind, async () =>
        {
            findButton!.IsEnabled = false;
            found.Children.Clear();
            found.Children.Add(new TextBlock { Text = _s.Syncing, Foreground = Ui.Faint, FontSize = 12 });
            try
            {
                var peers = await _host.DiscoverAsync(TimeSpan.FromSeconds(1.6));
                found.Children.Clear();
                var others = peers.Where(p => p.Id != _session.Config.DeviceId).ToList();
                if (others.Count == 0)
                {
                    found.Children.Add(new TextBlock { Text = _s.PairNone, Foreground = Ui.Faint, FontSize = 12, TextWrapping = TextWrapping.Wrap });
                }
                else
                {
                    foreach (var peer in others)
                    {
                        found.Children.Add(Ui.Button(peer.Display, () =>
                        {
                            address.Text = peer.Address;
                            code.Focus();
                        }));
                    }
                }
            }
            catch (Exception ex)
            {
                found.Children.Clear();
                found.Children.Add(new TextBlock { Text = ex.Message, Foreground = Ui.Danger, FontSize = 12 });
            }
            finally
            {
                findButton!.IsEnabled = true;
            }
        });

        panel.Children.Add(findButton);
        panel.Children.Add(found);

        Button? join = null;
        join = Ui.Button(_s.PairConnect, async () =>
        {
            join!.IsEnabled = false;
            // Do not let the 30-second sync send this vault before the fingerprints are confirmed.
            _pairConfirmation = true;
            resultText.Text = "";
            try
            {
                var peer = await _host.JoinAsync(_session, address.Text ?? "", code.Text ?? "", async info =>
                {
                    var ok = await ConfirmFingerprintsAsync(info.OwnFingerprint, info.Fingerprint);
                    if (ok) resultText.Text = _s.PairWaitingOther;
                    return ok;
                });

                resultText.Text = $"{_s.PairDone}: {peer.Label}\n{_s.PairFingerprint(peer.OwnFingerprint, peer.Fingerprint)}";
                await _host.SyncAsync(_session);
                RefreshList();
                UpdateMeta();
            }
            catch (Exception ex)
            {
                resultText.Text = ex.Message;
            }
            finally
            {
                _pairConfirmation = false;
                join!.IsEnabled = true;
            }
        }, primary: true);

        var joinRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        joinRow.Children.Add(address);
        joinRow.Children.Add(code);
        joinRow.Children.Add(join);
        panel.Children.Add(joinRow);
        panel.Children.Add(resultText);
        panel.Children.Add(new Border { Height = 1, Background = Ui.LineFaint, Margin = new Thickness(0, 6) });

        var close = Ui.Button(_s.Close, CloseOverlay);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(close);

        var card = Ui.Card(new ScrollViewer { Content = panel, MaxHeight = 560 }, 600);
        var dim = new Grid { Background = Ui.Dim, Children = { card } };
        dim.PointerPressed += (_, e) =>
        {
            if (e.Source == dim) CloseOverlay();
        };

        _root.Children.Add(dim);
        _overlay = dim;
    }

    /// <summary>
    /// The pairing code proves only that someone typed the code. Both users must compare the keys
    /// before the joiner's vault is sent to the other device.
    /// </summary>
    private Task<bool> ConfirmFingerprintsAsync(string own, string peer)
    {
        var tcs = new TaskCompletionSource<bool>();
        Grid? modal = null;

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        actions.Children.Add(Ui.Ghost(_s.Cancel, () =>
        {
            RemoveModal(modal!);
            tcs.TrySetResult(false);
        }));
        actions.Children.Add(Ui.Button(_s.PairMatch, () =>
        {
            RemoveModal(modal!);
            tcs.TrySetResult(true);
        }, primary: true));

        var panel = new StackPanel
        {
            Spacing = 10,
            Width = 380,
            Children =
            {
                new TextBlock
                {
                    Text = _s.PairCheckTitle,
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Ui.TextStrong,
                },
                new TextBlock
                {
                    Text = _s.PairCheckWarning,
                    FontSize = 12,
                    Foreground = Ui.Danger,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock
                {
                    Text = _s.PairFingerprint(own, peer),
                    FontSize = 12,
                    Foreground = Ui.Text,
                    TextWrapping = TextWrapping.Wrap,
                },
                actions,
            },
        };

        modal = AddModal(Ui.Card(panel, 460));
        return tcs.Task;
    }

    private void CloseOverlay()
    {
        if (_overlay is not null)
        {
            _root.Children.Remove(_overlay);
            _overlay = null;
        }

        if (_pairOffer is not null)
        {
            _ = _pairOffer.DisposeAsync();
            _pairOffer = null;
        }
    }
}
