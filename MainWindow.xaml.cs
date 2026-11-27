using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Kutu.Features.FileShare;

namespace Kutu;

public partial class MainWindow : Window
{
    private ShareServer? _server;
    private string? _folder;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _server?.Dispose();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要分享的文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        _folder = dialog.FolderName;
        FolderText.Text = _folder;
        StatusText.Text = "文件夹已选择。点击“开始分享”。";
        StartButton.IsEnabled = _server is null && !_busy;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_folder is null || !Directory.Exists(_folder))
        {
            StatusText.Text = "文件夹不存在，请重新选择。";
            return;
        }

        SetBusy(true);
        var server = new ShareServer(_folder);
        try
        {
            await server.StartAsync();
            _server = server;
            StatusText.Text = $"正在分享：{_folder}";
            RefreshAddresses();
        }
        catch (Exception ex)
        {
            server.Dispose();
            var socketError = ex.GetBaseException() as SocketException;
            StatusText.Text = socketError?.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse => "启动失败：端口 38765 已被其他程序使用。请先关闭占用该端口的程序。",
                SocketError.AccessDenied => "启动失败：系统阻止了网络监听。请检查安全软件和网络权限。",
                _ => $"启动失败：{ex.Message}"
            };
        }
        finally { SetBusy(false); }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_server is null) return;
        SetBusy(true);
        var server = _server;
        _server = null;
        string? stopError = null;
        try { await server.StopAsync(); }
        catch (Exception ex) { stopError = ex.Message; }
        finally
        {
            server.Dispose();
            AddressPicker.ItemsSource = null;
            UrlText.Text = "开始分享后显示地址";
            CopyStatusText.Text = "";
            StatusText.Text = stopError is null ? "分享已停止。" : $"停止时发生错误：{stopError}";
            SetBusy(false);
        }
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        RefreshAddresses();
        if (_server is null || AddressPicker.SelectedItem is not LocalAddress address)
        {
            CopyStatusText.Text = "未找到可用的局域网 IPv4 地址。请连接 Wi-Fi 或网线后再试。";
            return;
        }
        var url = $"http://{address.Ip}:{_server.Port}/";
        try
        {
            Clipboard.SetText(url);
            UrlText.Text = url;
            CopyStatusText.Text = "已复制当前地址，可以发给朋友。";
        }
        catch (Exception ex)
        {
            CopyStatusText.Text = $"剪贴板暂时不可用：{ex.Message}。可手动复制上方地址。";
        }
    }

    private void AddressPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_server is not null && AddressPicker.SelectedItem is LocalAddress address)
            UrlText.Text = $"http://{address.Ip}:{_server.Port}/";
    }

    private void RefreshAddresses()
    {
        var previous = (AddressPicker.SelectedItem as LocalAddress)?.Ip;
        var addresses = NetworkAddresses.GetCurrent();
        AddressPicker.ItemsSource = addresses;
        AddressPicker.SelectedItem = addresses.FirstOrDefault(x => Equals(x.Ip, previous)) ?? addresses.FirstOrDefault();
        if (addresses.Count == 0) UrlText.Text = "没有检测到可用的局域网地址";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ChooseButton.IsEnabled = !busy && _server is null;
        StartButton.IsEnabled = !busy && _server is null && _folder is not null;
        StopButton.IsEnabled = !busy && _server is not null;
        CopyButton.IsEnabled = !busy && _server is not null;
        AddressPicker.IsEnabled = !busy && _server is not null;
    }
}
