using ShmViewer.Core.Dat;

namespace ShmViewer.ViewModels;

public partial class DatDiffViewModel
{
    private void ReportFailure(string title, Exception error, string? context = null)
    {
        if (_disposed) return;
        var message = context == null ? error.Message : context + "\n" + error.Message;
        Status = title + ": " + message;
        ShowErrorNotice("DAT Diff · " + title, message);
    }
    private static Task<T> RunBackground<T>(Func<T> work, CancellationToken token = default) => Task.Run(() =>
    {
        var thread = Thread.CurrentThread; var previous = thread.Priority;
        try { thread.Priority = ThreadPriority.BelowNormal; token.ThrowIfCancellationRequested(); return work(); }
        finally { thread.Priority = previous; }
    }, token);
    internal (double Start, double Prepare, double Commit, double Finish) LastEditTiming { get; private set; }

    private CancellationToken BeginOperation(string message)
    {
        _cancellation?.Dispose(); _cancellation = new();
        _cancellableOperation = true; Status = message; IsBusy = true; NotifyState();
        return _cancellation.Token;
    }
    private async Task<bool> RunEditAsync(Func<DatDiffSession, CancellationToken, Task<DatPreparedChange?>> prepare,
        bool applyEditor = false, bool cellEditor = false, string failureTitle = "변경 실패")
    {
        if (!CanEdit || Session == null) return false;
        if ((!applyEditor && HasPendingEdit || !cellEditor && HasCellEdit) && !_confirm("적용하지 않은 입력을 버리고 이 작업을 수행하시겠습니까?")) return false;
        var timing = System.Diagnostics.Stopwatch.StartNew();
        var startTime = 0.0; var prepareTime = 0.0; var commitTime = 0.0;
        var completed = false;
        var session = Session; var token = BeginOperation("변경 내용 준비 중...");
        startTime = timing.Elapsed.TotalMilliseconds;
        try
        {
            var change = await prepare(session, token);
            prepareTime = timing.Elapsed.TotalMilliseconds;
            token.ThrowIfCancellationRequested();
            if (change == null || _disposed || Session != session) return false;
            RowsSnapshot? rows = null;
            if (change.Comparison != null)
            {
                Status = "비교 목록 준비 중...";
                rows = await RunBackground(() => PrepareRows(change.Comparison, ComparisonMode, token), token);
            }
            token.ThrowIfCancellationRequested();
            if (_disposed || Session != session) return false;
            commitTime = timing.Elapsed.TotalMilliseconds;
            session.Commit(change, token);
            ClearCellEdit(); HasPendingEdit = false; EditorError = string.Empty;
            if (rows != null) PublishRows(rows); else PublishValueChanges(change);
            completed = true; UpdateSummary(); commitTime = timing.Elapsed.TotalMilliseconds - commitTime; return true;
        }
        catch (OperationCanceledException) { if (!_disposed) Status = "작업을 취소했습니다."; return false; }
        catch (Exception ex)
        {
            if (_disposed) return false;
            if (cellEditor) CellEditorError = ex.Message; else EditorError = ex.Message;
            if (applyEditor) Status = "변경 실패: " + ex.Message;
            else ReportFailure(failureTitle, ex);
            return false;
        }
        finally { if (!_disposed) { var finish = timing.Elapsed.TotalMilliseconds; IsBusy = false; if (completed) LoadEditor(); NotifyState(); LastEditTiming = (startTime, prepareTime - startTime, commitTime, timing.Elapsed.TotalMilliseconds - finish); } }
    }
    private async Task RefreshRowsAsync(string? togglePath = null, bool toggleExpanded = false, IReadOnlyList<string>? expandPaths = null)
    {
        if (!CanInteract) return;
        var session = Session; var token = BeginOperation("비교 목록 준비 중...");
        try
        {
            var rows = await Task.Run(() => PrepareRows(session?.Comparison, ComparisonMode, token, togglePath: togglePath, toggleExpanded: toggleExpanded, expandPaths: expandPaths), token);
            token.ThrowIfCancellationRequested();
            if (!_disposed && Session == session) PublishRows(rows);
        }
        catch (OperationCanceledException) { Status = "목록 갱신을 취소했습니다."; }
        catch (Exception ex) { ReportFailure("목록 갱신 실패", ex); }
        finally { if (!_disposed) IsBusy = false; }
    }
}
