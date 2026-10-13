using System.Text;

namespace VictusFanControl.App;

internal static class ProductFanAnimationSettingsSelfTest
{
    internal static void Run(Action<bool,string> require)
    {
        var root=Path.Combine(Path.GetTempPath(),"vfc-animation-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"fan-animation.json");
            var settings=new ProductFanAnimationSettings(path);
            require(File.Exists(path)&&settings.SecondsPerRevolution==1,"First start did not create the editable default file.");
            string Json(string number)=>"{\""+ProductFanAnimationSettings.Key+"\":"+number+"}";
            File.WriteAllText(path,Json("2"));require(settings.Reload()&&settings.SecondsPerRevolution==2,"Saving the file did not reload a new speed.");
            require(!settings.Reload(),"Unchanged settings caused a redundant update.");
            var original=File.ReadAllBytes(path);var reopened=new ProductFanAnimationSettings(path);
            require(reopened.SecondsPerRevolution==2&&File.ReadAllBytes(path).SequenceEqual(original),"Restart overwrote the user's custom setting.");
            foreach(var invalid in new[]{"", "{", "[]", "{}", Json("0"),Json("-1"),Json("0.09"),Json("61"),Json("1e999"),Json("\"2\""),
                "{\"unknown\":2}","{\""+ProductFanAnimationSettings.Key+"\":2,\""+ProductFanAnimationSettings.Key+"\":3}",
                Json("2")+new string(' ',5000)})
            {
                File.WriteAllText(path,invalid);
                require(!settings.Reload()&&settings.SecondsPerRevolution==2,"Invalid/partial/oversized JSON replaced the last valid setting.");
            }
            File.WriteAllText(path,Json("0.5"),new UTF8Encoding(true));
            require(settings.Reload()&&settings.SecondsPerRevolution==0.5,"UTF-8 BOM or recovery from a partial save failed.");
            foreach(var value in new[]{"0.1","60","1.5"})
            {
                File.WriteAllText(path,Json(value));require(settings.Reload(),"A valid boundary/fractional setting was rejected.");
            }
            File.Delete(path);require(!settings.Reload()&&settings.SecondsPerRevolution==1.5&&!File.Exists(path),
                "Missing file reset the running value or silently rewrote the file.");
            Directory.CreateDirectory(path);var unavailable=new ProductFanAnimationSettings(path);
            require(unavailable.SecondsPerRevolution==1&&!unavailable.Reload(),"Unavailable storage crashed or fabricated a custom setting.");
            require(!ProductFanAnimationSettings.IsValid(double.NaN)&&!ProductFanAnimationSettings.IsValid(double.PositiveInfinity),
                "Non-finite reference speed was accepted.");
        }
        finally {Directory.Delete(root,true);}
        Console.WriteLine("Live fan animation settings: PASS (editable default, fractional/boundary values, saved reload, restart preservation, bounded JSON/BOM, invalid and partial writes, missing/unavailable files; isolated temp storage).");
    }
}
