using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace AsyncFixture;

// Blocking reads behind a completion check, issue #613. Methods named Guarded* hold only reads
// that run once the receiver is complete and must report nothing. Methods named Unguarded* hold
// exactly one read find_async_violations must still report.
public class CompletionGuards
{
    private Task<int> _task = Task.FromResult(1);

    private Task<int> TaskProperty => _task;

    private static async Task<int> Slow(ValueTask<int> vt) => await vt;

    private static Task<int> Other() => Task.FromResult(2);

    // ============ GUARDED (must NOT be flagged) ============

    // The repro from the issue: the fast path is skipped, the slow path is reported.
    public static int ValueTaskFastPath(ValueTask<int> vt)
    {
        if (vt.IsCompletedSuccessfully)
            return vt.GetAwaiter().GetResult();
        return Slow(vt).GetAwaiter().GetResult();
    }

    public static int GuardedIfIsCompleted(Task<int> t)
    {
        if (t.IsCompleted)
        {
            return t.GetAwaiter().GetResult();
        }
        return 0;
    }

    public static int GuardedAndChain(Task<int> t, bool flag)
    {
        if (flag && (t.IsCompleted && t.Id > 0))
            return t.GetAwaiter().GetResult();
        return 0;
    }

    public static int GuardedStatusRanToCompletion(Task<int> t)
    {
        if (t.Status == TaskStatus.RanToCompletion)
            return t.GetAwaiter().GetResult();
        return 0;
    }

    public static int GuardedStatusReversed(Task<int> t)
    {
        if (TaskStatus.RanToCompletion == t.Status)
            return t.Result;
        return 0;
    }

    public static int GuardedConditionalTrueArm(ValueTask<int> vt) =>
        vt.IsCompletedSuccessfully ? vt.GetAwaiter().GetResult() : 0;

    public static int GuardedConditionalFalseArm(ValueTask<int> vt) =>
        !vt.IsCompleted ? 0 : vt.Result;

    public static int GuardedEarlyReturn(Task<int> t)
    {
        if (!t.IsCompleted) return -1;
        Console.WriteLine();
        return t.GetAwaiter().GetResult();
    }

    public static int GuardedEarlyThrowBlock(Task<int> t)
    {
        if (t.Status != TaskStatus.RanToCompletion)
        {
            Console.WriteLine();
            throw new InvalidOperationException();
        }
        return t.Result;
    }

    public static int GuardedEarlyExitOrChain(Task<int> t, bool flag)
    {
        if (flag || !t.IsCompletedSuccessfully)
            return 0;
        return t.Result;
    }

    public static int GuardedEarlyExitOuterBlock(Task<int> t, bool flag)
    {
        if (!t.IsCompleted) return 0;
        if (flag)
        {
            return (t).Result;
        }
        return 1;
    }

    public static int GuardedContinue(List<Task<int>> tasks)
    {
        var sum = 0;
        foreach (var t in tasks)
        {
            if (!t.IsCompleted) continue;
            sum += t.Result;
        }
        return sum;
    }

    public static int GuardedElseBranch(Task<int> t)
    {
        int value;
        if (!t.IsCompleted)
        {
            value = 0;
        }
        else
        {
            value = t.Result;
        }
        return value;
    }

    public static int GuardedConfigureAwait(Task<int> t)
    {
        if (t.IsCompleted)
            return t.ConfigureAwait(false).GetAwaiter().GetResult();
        return 0;
    }

    public static int GuardedAwaiterLocal(Task<int> t)
    {
        var awaiter = t.GetAwaiter();
        if (awaiter.IsCompleted)
            return awaiter.GetResult();
        return 0;
    }

    public int GuardedField()
    {
        if (this._task.IsCompleted)
            return _task.Result;
        return 0;
    }

    public static bool GuardedLogicalAnd(Task<bool> t) => t.IsCompleted && t.Result;

    public static bool GuardedLogicalOr(Task<bool> t) => !t.IsCompleted || t.GetAwaiter().GetResult();

    // ============ UNGUARDED (each must be flagged exactly once) ============

    public static int UnguardedWrongReceiver(Task<int> a, Task<int> b)
    {
        if (a.IsCompleted)
            return b.GetAwaiter().GetResult();
        return 0;
    }

    public static int UnguardedOrChain(Task<int> t, bool flag)
    {
        if (flag || t.IsCompleted)
            return t.GetAwaiter().GetResult();
        return 0;
    }

    public static int UnguardedNegatedOnTrueBranch(Task<int> t)
    {
        if (!t.IsCompleted)
            return t.GetAwaiter().GetResult();
        return 0;
    }

    public static int UnguardedConditionalFalseArm(Task<int> t) =>
        t.IsCompleted ? 0 : t.Result;

    public static int UnguardedElseOfPositiveGuard(Task<int> t)
    {
        if (t.IsCompleted)
            return 0;
        else
            return t.Result;
    }

    public static int UnguardedStatusOtherValue(Task<int> t)
    {
        if (t.Status == TaskStatus.Faulted)
            return 0;
        return t.Result;
    }

    public static int UnguardedIfWithoutExit(Task<int> t)
    {
        if (!t.IsCompleted)
            Console.WriteLine();
        return t.Result;
    }

    // Conservative: an if with an else is not treated as an early exit.
    public static int UnguardedEarlyExitWithElse(Task<int> t)
    {
        if (!t.IsCompleted)
            return 0;
        else
            Console.WriteLine();
        return t.Result;
    }

    public static int UnguardedEmptyExitBlock(Task<int> t)
    {
        if (!t.IsCompleted)
        {
        }
        return t.Result;
    }

    public static int UnguardedReadBeforeExit(Task<int> t)
    {
        var value = t.Result;
        if (!t.IsCompleted) return 0;
        return value;
    }

    public static int UnguardedReassignedInBranch(Task<int> t)
    {
        if (t.IsCompleted)
        {
            t = Other();
            return t.GetAwaiter().GetResult();
        }
        return 0;
    }

    public static int UnguardedReassignedAfterExit(Task<int> t)
    {
        if (!t.IsCompleted) return 0;
        Replace(ref t);
        return t.Result;
    }

    // Conservative: a lambda may run after the captured receiver has been reassigned.
    public static int UnguardedInsideLambda(Task<int> t)
    {
        if (t.IsCompleted)
        {
            Func<int> read = () => t.Result;
            return read();
        }
        return 0;
    }

    public static int UnguardedAwaiterFromCall(Task<int> t)
    {
        if (t.IsCompleted)
            return AwaiterOf(t).GetResult();
        return 0;
    }

    public int UnguardedProperty()
    {
        if (TaskProperty.IsCompleted)
            return TaskProperty.Result;
        return 0;
    }

    private static void Replace(ref Task<int> t) => t = Other();

    private static TaskAwaiter<int> AwaiterOf(Task<int> t) => t.GetAwaiter();
}
