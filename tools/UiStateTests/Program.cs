using System.Reflection;
using FFXIVInjector.UI.ViewModels;
var vm=new MainViewModel();
var changes=new List<string>();vm.PropertyChanged+=(_,e)=>changes.Add(e.PropertyName??"");
using var cancellation=new CancellationTokenSource();
typeof(MainViewModel).GetField("_injectCts",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(vm,cancellation);
vm.IsInjecting=true;Check(vm.CanCancel&&vm.CanInjectOrCancel,"Preparation must remain cancellable");
var progress=typeof(MainViewModel).GetMethod("UpdateInjectionProgress",BindingFlags.Instance|BindingFlags.NonPublic)!;
progress.Invoke(vm,new object[]{100d});Check(vm.Progress==99,"Preparation must not claim overall completion");
vm.IsCommitting=true;vm.Progress=0;vm.IsIndeterminate=true;
Check(!vm.CanCancel&&!vm.CanInjectOrCancel,"Final application must disable cancellation");
Check(changes.Contains(nameof(vm.CanCancel))&&changes.Contains(nameof(vm.CanInjectOrCancel)),"Button bindings must receive state updates");
progress.Invoke(vm,new object[]{100d});Check(vm.Progress==0&&vm.IsIndeterminate,"Late progress must not expose 100% during final application");
await vm.InjectCommand.ExecuteAsync(null);Check(!cancellation.IsCancellationRequested,"Even a direct cancel command must be ignored during commit");
vm.IsInjecting=false;vm.IsCommitting=false;progress.Invoke(vm,new object[]{75d});Check(vm.Progress==0,"Late callbacks must not change completed state");
Check(vm.CanInjectOrCancel&&!vm.CanCancel,"Button must recover after final application");
Console.WriteLine("PASS: final-stage cancellation guard, button notifications, preparation cap, and late progress callbacks.");
static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
