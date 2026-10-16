using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.VisualBasic.FileIO;

namespace VideoBatch {
    public sealed class TelegramRecipientList {
        public List<string> Users=new List<string>();
        public int Invalid,Duplicates;
        public string Text=>string.Join(Environment.NewLine,Users);
    }
    public static class TelegramRecipients {
        public static string Normalize(string value){
            string s=(value??"").Trim();s=Regex.Replace(s,@"^https?://(?:www\.)?t\.me/","",RegexOptions.IgnoreCase).TrimStart('@').TrimEnd('/');
            if(Regex.IsMatch(s,@"^[A-Za-z][A-Za-z0-9_]{3,31}$"))return s;
            if(Regex.IsMatch(s,@"^\d+$")&&long.TryParse(s,out var id)&&id>0)return id.ToString(CultureInfo.InvariantCulture);
            return null;
        }
        public static TelegramRecipientList Read(string path){
            var file=new FileInfo(path);if(file.Length>4*1024*1024)throw new IOException("Список больше 4 МБ. Разделите его на части.");
            string text;
            try{using(var reader=new StreamReader(path,new UTF8Encoding(false,true),true))text=reader.ReadToEnd();}
            catch(DecoderFallbackException){text=File.ReadAllText(path,Encoding.GetEncoding(1251));}
            return Parse(text,Path.GetExtension(path));
        }
        public static TelegramRecipientList Parse(string text,string extension=".txt"){
            var result=new TelegramRecipientList();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int count=0;
            void add(string raw){if(++count>10000)throw new IOException("В списке больше 10 000 строк");if(string.IsNullOrWhiteSpace(raw))return;var s=Normalize(raw);if(s==null){result.Invalid++;return;}if(!seen.Add(s)){result.Duplicates++;return;}result.Users.Add(s);}
            if(extension.Equals(".json",StringComparison.OrdinalIgnoreCase)){
                object root;try{root=new JavaScriptSerializer{MaxJsonLength=4*1024*1024}.DeserializeObject(text);}catch{throw new IOException("Некорректный JSON списка пользователей");}
                if(root is Dictionary<string,object> obj)root=obj.TryGetValue("users",out var values)?values:null;
                if(!(root is object[] rows))throw new IOException("JSON: нужен массив пользователей или объект с массивом users");
                foreach(var row in rows){if(row is Dictionary<string,object> d){object value=null;foreach(string key in new[]{"username","user_id","id"})if(d.TryGetValue(key,out var v)&&!string.IsNullOrWhiteSpace(Convert.ToString(v))){value=v;break;}add(Convert.ToString(value)??"?");}else add(row is bool||row==null?"?":Convert.ToString(row));}
            }else if(extension.Equals(".csv",StringComparison.OrdinalIgnoreCase)){
                using(var parser=new TextFieldParser(new StringReader(text))){parser.TextFieldType=FieldType.Delimited;parser.SetDelimiters(text.Split('\n')[0].Contains(';')?";":",");parser.HasFieldsEnclosedInQuotes=true;
                    string[] first=parser.EndOfData?new string[0]:parser.ReadFields();int column=-1;
                    foreach(string alias in new[]{"username","user_id","telegram_id","id","имя пользователя","id пользователя"}){column=Array.FindIndex(first,v=>v.Trim().TrimStart('\uFEFF').Equals(alias,StringComparison.OrdinalIgnoreCase));if(column>=0)break;}
                    if(column<0){if(first.Length!=1)throw new IOException("CSV: добавьте заголовок username или user_id");add(first[0]);column=0;}
                    while(!parser.EndOfData){var row=parser.ReadFields();add(row.Length>column?row[column]:"?");}
                }
            }else foreach(var row in Regex.Split(text.TrimStart('\uFEFF'),@"[\s,;]+"))add(row);
            return result;
        }
    }
}
