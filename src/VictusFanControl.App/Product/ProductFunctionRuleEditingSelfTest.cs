using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static class ProductFunctionRuleEditingSelfTest
{
    private static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    internal static async Task<int> RunAsync()
    {
        try
        {
            // Exercise the production editor commands and offscreen drawing.
            // Never Show, create a ProductForm, or construct a hardware runtime.
            MenuSelections();
            CanvasStates();
            // Constructing WinForms controls can install a message-loop context.
            // This headless entry intentionally has no message pump.
            SynchronizationContext.SetSynchronizationContext(null);
            await SaveOrderingAndFailuresAsync().ConfigureAwait(false);
            Console.WriteLine("PASS function editing: inline production choices, custom-policy preservation, apply/save ordering, isolated real-file errors, retry, and unchanged unrelated settings. No desktop or hardware IO.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static void MenuSelections()
    {
        var initial=new ProductProfiles{Functions=new ProductFunctionPolicies().Unlinked()};
        using var canvas=new ProductCanvas{Page=ProductPage.Functions};
        void Edit(ProductProfiles next)=>canvas.Profiles=next;
        var count=0;
        foreach(var function in Enum.GetValues<ProductFunction>())
        foreach(var binding in Enum.GetValues<ProductFunctionBinding>())
        {
            var draft=initial with{Functions=initial.Functions!.With(function,new(ProductFunctionBinding.FollowAutomatic,false,true,false))};
            canvas.Profiles=draft;canvas.SelectedFunction=function;
            var untouched=ProductProfilesStore.Serialize(draft);
            Require(canvas.HandleFunctionEditorCommand($"function-{(int)function}-rule",Edit)&&
                canvas.FunctionChooser==ProductFunctionChooser.Binding&&Application.OpenForms.Count==0&&ProductProfilesStore.Serialize(canvas.Profiles)==untouched,
                "Opening the inline choices changed the draft or showed a Form.");
            canvas.HandleFunctionEditorCommand($"function-{(int)function}-choose-{(int)binding}",Edit);
            var selected=canvas.Profiles.FunctionPolicies().Get(function);
            Require(selected.Binding==binding&&selected.StartWithApplication&&selected.SelectedForLinkedStart==(binding!=ProductFunctionBinding.Independent)&&
                selected.PreserveUnselectedOnLinkedStart&&canvas.FunctionChooser==ProductFunctionChooser.None,
                "An inline command changed the wrong rule, erased startup, or left the chooser open.");
            foreach(var peer in Enum.GetValues<ProductFunction>().Where(x=>x!=function))
                Require(canvas.Profiles.FunctionPolicies().Get(peer)==initial.Functions!.Get(peer),"Changing one rule changed a peer.");
            count++;
        }
        foreach(var mode in Enum.GetValues<ProductExternalOutputMode>())
        {
            canvas.Profiles=initial;canvas.SelectedFunction=ProductFunction.ExternalCooling;
            canvas.HandleFunctionEditorCommand("functions-base-mode",Edit);
            canvas.HandleFunctionEditorCommand($"functions-base-choice-{(int)mode}",Edit);
            Require(canvas.Profiles.FunctionPolicies().ExternalOutputMode==mode&&canvas.Profiles.FunctionPolicies().CpuLimits==initial.Functions!.CpuLimits&&
                canvas.FunctionChooser==ProductFunctionChooser.None,"Base mode choices changed CPU rules or left the chooser open.");
            count++;
        }
        var before=ProductProfilesStore.Serialize(canvas.Profiles);
        foreach(var stale in new[]{"function-0-choose-1","function-3-choose-99","functions-base-choice-1","functions-base-choice-999"})
            canvas.HandleFunctionEditorCommand(stale,Edit);
        Require(ProductProfilesStore.Serialize(canvas.Profiles)==before,"A stale or invalid choice changed the draft.");
        canvas.HandleFunctionEditorCommand("function-3-rule",Edit);
        foreach(var invalid in new[]{"function-0-choose-1","function-3-choose-999","function-3-choose-invalid","functions-base-choice-1"})
            canvas.HandleFunctionEditorCommand(invalid,Edit);
        Require(ProductProfilesStore.Serialize(canvas.Profiles)==before&&canvas.FunctionChooser==ProductFunctionChooser.Binding,
            "An invalid or wrong-panel choice changed the draft or closed valid options.");
        canvas.Busy=true;canvas.HandleFunctionEditorCommand("function-3-choose-1",Edit);
        Require(ProductProfilesStore.Serialize(canvas.Profiles)==before,"A busy command changed the draft.");canvas.Busy=false;
        canvas.HandleKey(Keys.Escape);Require(canvas.FunctionChooser==ProductFunctionChooser.None,"Escape did not close choices.");
        canvas.HandleFunctionEditorCommand("function-3-rule",Edit);canvas.HandleFunctionEditorCommand("function-0-select",Edit);
        Require(canvas.FunctionChooser==ProductFunctionChooser.None&&canvas.SelectedFunction==ProductFunction.CpuLimits,"Function navigation retained stale options.");
        canvas.HandleFunctionEditorCommand("function-0-rule",Edit);canvas.FunctionAdvanced=true;
        Require(canvas.FunctionChooser==ProductFunctionChooser.None,"Advanced retained hidden choices.");canvas.FunctionAdvanced=false;
        canvas.HandleFunctionEditorCommand("function-0-rule",Edit);canvas.Page=ProductPage.Home;
        Require(canvas.FunctionChooser==ProductFunctionChooser.None,"Leaving Functions retained stale choices.");
        canvas.Page=ProductPage.Functions;canvas.SelectedFunction=ProductFunction.ExternalCooling;
        for(var i=0;i<200;i++)
        {
            canvas.HandleFunctionEditorCommand("function-3-rule",Edit);
            canvas.HandleKey(Keys.Escape);
            canvas.HandleFunctionEditorCommand("functions-base-mode",Edit);
            canvas.HandleFunctionEditorCommand("functions-base-mode",Edit);
        }
        Require(canvas.FunctionChooser==ProductFunctionChooser.None&&ProductProfilesStore.Serialize(canvas.Profiles)==before,
            "Repeated opening/cancellation changed settings or retained a chooser.");
        Require(!canvas.IsHandleCreated&&Application.OpenForms.Count==0,"Headless selections created a visible control handle.");
        Console.WriteLine($"PASS {count} production inline selections; stale/invalid/busy fences, Escape, navigation and 400 cancelled open/close cycles without native handles; no windows or device IO.");
    }
    private static void CanvasStates()
    {
        var profiles=new ProductProfiles{Functions=new ProductFunctionPolicies().Unlinked()};
        using var canvas=new ProductCanvas{Page=ProductPage.Functions,Profiles=profiles,
            State=new(){Runtime="Healthy",Source="Ac",FunctionPolicies=profiles.Functions!},SavedFunctionRules=()=>profiles.Functions!};
        var count=0;
        foreach(var size in new[]{new Size(1000,560),new Size(1280,800),new Size(1920,1080)})
        foreach(var function in Enum.GetValues<ProductFunction>())
        foreach(var view in new[]{"normal","advanced","binding","base-mode"})
        foreach(var busy in new[]{false,true})
        {
            if(view=="base-mode"&&function!=ProductFunction.ExternalCooling)continue;
            var advanced=view=="advanced";
            canvas.Busy=false;
            canvas.Size=size;canvas.SelectedFunction=function;canvas.FunctionAdvanced=advanced;
            if(view=="binding")canvas.HandleFunctionEditorCommand($"function-{(int)function}-rule",_=>throw new InvalidOperationException("Opening choices edited the draft."));
            if(view=="base-mode")canvas.HandleFunctionEditorCommand("functions-base-mode",_=>throw new InvalidOperationException("Opening base modes edited the draft."));
            canvas.Busy=busy;
            using var bitmap=new Bitmap(size.Width,size.Height);using var g=Graphics.FromImage(bitmap);
            typeof(ProductCanvas).GetMethod("PaintCanvas",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
                .Invoke(canvas,new object[]{new PaintEventArgs(g,new Rectangle(Point.Empty,size))});
            var hits=canvas.Hits;
            Require(hits.Any(x=>x.Id=="functions-apply-save")&&hits.Count(x=>x.Id.EndsWith("-select",StringComparison.Ordinal))==4,
                "Required function navigation or save action is absent.");
            Require(hits.Any(x=>x.Id==$"function-{(int)function}-rule")==(!advanced&&view!="base-mode")&&
                hits.Any(x=>x.Id==$"function-{(int)function}-linked")==advanced,
                "Hidden main/advanced controls retained clickable overlays.");
            var functional=hits.Where(x=>x.Id.StartsWith("function-",StringComparison.Ordinal)||x.Id.StartsWith("functions-",StringComparison.Ordinal)).ToArray();
            Require(functional.Select(x=>x.Id).Distinct().Count()==functional.Length,"Duplicate action identities.");
            Require(functional.All(x=>x.Bounds.Left>=310&&x.Bounds.Right<=1640&&x.Bounds.Top>=180&&x.Bounds.Bottom<=842),"Function actions escape their surface.");
            Require(!busy||functional.All(x=>!x.Enabled),"A busy editor left an actionable control.");
            Require(hits.Count(x=>x.Id.Contains("-choose-",StringComparison.Ordinal))==(view=="binding"?5:0)&&
                hits.Count(x=>x.Id.StartsWith("functions-base-choice-",StringComparison.Ordinal))==(view=="base-mode"?3:0),"Inline option count is incorrect.");
            Require(hits.Any(x=>x.Id==$"function-{(int)function}-startup")==(!advanced&&view=="normal"),"Covered startup control retained a hit target.");
            if(view=="binding")Require(hits.Count(x=>x.Id.Contains("-choose-",StringComparison.Ordinal)&&x.Label.StartsWith("✓ ",StringComparison.Ordinal))==1,"Rule choices have no unique current mark.");
            if(view=="base-mode")Require(hits.Count(x=>x.Id.StartsWith("functions-base-choice-",StringComparison.Ordinal)&&x.Label.StartsWith("✓ ",StringComparison.Ordinal))==1,"Base choices have no unique current mark.");
            for(var i=0;i<functional.Length;i++)for(var j=i+1;j<functional.Length;j++)
                Require(!functional[i].Bounds.IntersectsWith(functional[j].Bounds),"Function action targets overlap.");
            count++;
        }
        canvas.Busy=false;canvas.SelectedFunction=ProductFunction.CpuLimits;canvas.FunctionAdvanced=false;
        // Route a real keyboard activation through the same Canvas.Command event
        // as ProductForm, without focusing or showing any desktop window.
        var changed=0;
        canvas.Command+=id=>canvas.HandleFunctionEditorCommand(id,next=>{changed++;canvas.Profiles=next;});
        void Paint()
        {using var bitmap=new Bitmap(canvas.Width,canvas.Height);using var g=Graphics.FromImage(bitmap);
            typeof(ProductCanvas).GetMethod("PaintCanvas",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
                .Invoke(canvas,new object[]{new PaintEventArgs(g,new Rectangle(Point.Empty,canvas.Size))});}
        Paint();
        void Activate(string id)
        {
            // Track the canvas keyboard cursor without focusing the control.
            // Accessibility Focused also requires real desktop focus, which
            // this headless check deliberately never requests.
            for(var i=0;i<canvas.Hits.Count+1;i++)
            {
                var index=canvas.Hits.ToList().FindIndex(h=>h.Id==id);
                Require(index>=0,"Keyboard target is absent.");
                if((string?)typeof(ProductCanvas).GetField("_keyboardId",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(canvas)==id)
                {canvas.HandleKey(Keys.Enter);return;}
                canvas.HandleKey(Keys.Tab);
            }
            throw new InvalidOperationException("Keyboard could not reach inline choice.");
        }
        Activate("function-0-rule");Paint();Activate("function-0-choose-1");Paint();
        Require(changed==1&&canvas.Profiles.FunctionPolicies().CpuLimits.Binding==ProductFunctionBinding.StartWithAutomatic&&
            canvas.FunctionChooser==ProductFunctionChooser.None,"Keyboard activation did not select the inline rule.");
        Activate("function-3-select");Paint();Activate("functions-base-mode");Paint();Activate("functions-base-choice-1");Paint();
        Require(changed==2&&canvas.Profiles.FunctionPolicies().ExternalOutputMode==ProductExternalOutputMode.Fixed&&
            canvas.FunctionChooser==ProductFunctionChooser.None,"Keyboard activation did not select the base mode.");
        Require(!canvas.IsHandleCreated&&Application.OpenForms.Count==0,"Keyboard tests created a desktop control handle.");
        canvas.Profiles=profiles;
        canvas.Dirty=true;Require(!canvas.FunctionRulesPending,"An unrelated draft falsely marks function rules pending.");
        canvas.Profiles=profiles with{Functions=profiles.Functions! with{ExternalFixedOutput=87}};
        Require(canvas.FunctionRulesPending,"An unapplied function edit has no pending marker.");
        canvas.State=canvas.State with{FunctionPolicies=canvas.Profiles.Functions!};
        Require(canvas.FunctionRulesPending,"Applied but unsaved rules lost their pending marker.");
        canvas.SavedFunctionRules=()=>canvas.Profiles.Functions!;
        Require(!canvas.FunctionRulesPending,"Saved and applied rules remain incorrectly pending.");
        Console.WriteLine($"PASS {count} offscreen function layouts, busy controls, target separation, hidden controls, unique selection, keyboard commands and independent pending markers; no window shown.");
    }
    private static async Task SaveOrderingAndFailuresAsync()
    {
        var saved=new ProductProfiles{Ac=ProductProfiles.DefaultProfile(ProductPowerProfile.Ac) with{CpuPl1Watts=28,CpuPl2Watts=38},
            StartMinimized=true,ActivateAutomaticOnStart=true,Functions=new ProductFunctionPolicies().Unlinked()};
        var rules=saved.Functions! with{Ctgp=new(ProductFunctionBinding.StartWithAutomatic,true,true),ExternalFixedOutput=87};
        var folder=Path.Combine(Path.GetTempPath(),"Victus-function-rules-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"profiles.json");ProductProfilesStore.Save(saved,path);var before=File.ReadAllBytes(path);
        var events=new List<string>();
        var result=await ProductFunctionRuleEditing.ApplyAndSaveAsync(saved,rules,p=>
        {Require(p==rules,"Apply received different rules.");events.Add("apply");return Task.CompletedTask;},p=>
        {events.Add("save");ProductProfilesStore.Save(p,path);});
        Require(events.SequenceEqual(new[]{"apply","save"}),"Save ran before successful Apply.");
        Require(ProductProfilesStore.Serialize(result with{Functions=saved.Functions})==ProductProfilesStore.Serialize(saved),
            "Rule saving changed unrelated caps, curves, startup or preferences.");
        Require(ProductProfilesStore.Load(path,out _).Functions==rules,"Success was returned without durable rules.");
        Require(saved.Functions!=rules,"Applying mutated the saved baseline before committing it.");
        ProductProfilesStore.Save(saved,path);events.Clear();var rejected=false;
        try{await ProductFunctionRuleEditing.ApplyAndSaveAsync(saved,rules,_=>Task.FromException(new IOException("Fixture Apply refusal")),p=>events.Add("save"));}
        catch(IOException error){rejected=error.Message=="Fixture Apply refusal";}
        Require(rejected&&events.Count==0&&File.ReadAllBytes(path).SequenceEqual(before),"Rejected Apply still saved preferences or hid its error.");
        var blocked=Path.Combine(folder,"directory-not-file");Directory.CreateDirectory(blocked);events.Clear();var partial=false;
        try{await ProductFunctionRuleEditing.ApplyAndSaveAsync(saved,rules,_=>{events.Add("apply");return Task.CompletedTask;},p=>ProductProfilesStore.Save(p,blocked));}
        catch(IOException error){partial=error.Message.Contains("NO guardadas",StringComparison.Ordinal);}
        Require(partial&&events.SequenceEqual(new[]{"apply"})&&File.ReadAllBytes(path).SequenceEqual(before),
            "A real save failure fabricated combined success or altered the old file.");
        result=await ProductFunctionRuleEditing.ApplyAndSaveAsync(saved,rules,_=>Task.CompletedTask,p=>ProductProfilesStore.Save(p,path));
        Require(result.Functions==rules&&ProductProfilesStore.Load(path,out _).Functions==rules,"Explicit retry after save failure did not persist.");
        Console.WriteLine("PASS actual temporary-file save, Apply refusal, disk-path failure, truthful partial outcome and explicit retry; settings/startup outside the rules preserved.");
    }
}
