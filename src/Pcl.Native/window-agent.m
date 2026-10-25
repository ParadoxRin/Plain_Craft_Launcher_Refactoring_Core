#import <Cocoa/Cocoa.h>
#import <dispatch/dispatch.h>
#import <sys/resource.h>
#import <mach/mach.h>
#import <mach/task_policy.h>

// JVM 启动时加载。只操作本 Java 进程自己的可见游戏窗口，不使用辅助功能权限或控制其他应用。
__attribute__((visibility("default"))) int Agent_OnLoad(void *vm, char *options, void *reserved)
{
    @autoreleasepool
    {
        NSString *file = options ? [NSString stringWithUTF8String:options] : nil;
        NSData *data = file ? [NSData dataWithContentsOfFile:file] : nil;
        NSDictionary *settings = data ? [NSJSONSerialization JSONObjectWithData:data options:0 error:nil] : nil;
        if (![settings isKindOfClass:[NSDictionary class]])
        {
            return -1;
        }
        NSString *title = settings[@"title"];
        BOOL maximize = [settings[@"maximize"] boolValue];
        NSString *receipt = settings[@"receipt"];
        int priority = [settings[@"priority"] intValue];
        // 未提权的普通应用可以降低优先级；提高到负 nice 需要系统授权，因此最高选项保持正常优先级。
        if (priority > 0)
        {
            setpriority(PRIO_PROCESS, 0, priority);
        }
        // 前台角色由内核调度策略处理，无需 root；失败结果随回执保存，不声称实时优先级。
        BOOL foreground = [settings[@"foreground"] boolValue];
        kern_return_t roleResult = KERN_SUCCESS;
        if (foreground)
        {
            task_category_policy_data_t policy = { TASK_FOREGROUND_APPLICATION };
            roleResult = task_policy_set(mach_task_self(), TASK_CATEGORY_POLICY, (task_policy_t)&policy, TASK_CATEGORY_POLICY_COUNT);
        }
        static dispatch_source_t timer;
        timer = dispatch_source_create(DISPATCH_SOURCE_TYPE_TIMER, 0, 0, dispatch_get_main_queue());
        dispatch_source_set_timer(timer, dispatch_time(DISPATCH_TIME_NOW, NSEC_PER_SEC), NSEC_PER_SEC, NSEC_PER_SEC / 10);
        __block BOOL sized = NO;
        __block BOOL recorded = NO;
        dispatch_source_set_event_handler(timer, ^{
            @autoreleasepool
            {
                for (NSWindow *window in NSApp.windows)
                {
                    if (!window.isVisible || window.contentView.bounds.size.width < 320 || window.contentView.bounds.size.height < 240)
                    {
                        continue;
                    }
                    if (title.length > 0)
                    {
                        window.title = title;
                    }
                    if (maximize && !sized && !(window.styleMask & NSWindowStyleMaskFullScreen))
                    {
                        [window setFrame:(window.screen ?: NSScreen.mainScreen).visibleFrame display:YES];
                        sized = YES;
                    }
                    if (!recorded && receipt.length > 0)
                    {
                        NSDictionary *result = @{@"title": window.title ?: @"", @"width": @(window.frame.size.width), @"height": @(window.frame.size.height), @"maximized": @(sized), @"nice": @(getpriority(PRIO_PROCESS, 0)), @"pid": @(getpid()), @"foregroundRequested": @(foreground), @"roleResult": @(roleResult)};
                        NSData *json = [NSJSONSerialization dataWithJSONObject:result options:NSJSONWritingPrettyPrinted error:nil];
                        [json writeToFile:receipt atomically:YES];
                        recorded = YES;
                    }
                    break;
                }
            }
        });
        dispatch_resume(timer);
    }
    return 0;
}
