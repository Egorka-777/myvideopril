namespace VideoBatch {
    public static class UploadItemState {
        public const string Pending="pending";
        public const string Uploading="uploading";
        public const string Scheduling="scheduling";
        public const string Scheduled="scheduled";
        public const string Error="error";
        public const string Unknown="unknown";

        public static string Normalize(string state){
            string s=(state??"").Trim().ToLowerInvariant();
            if(string.IsNullOrWhiteSpace(s))return Pending;
            if(s==Scheduled||s=="отложено")return Scheduled;
            if(s==Error||s=="ошибка")return Error;
            if(s==Unknown)return Unknown;
            if(s==Uploading||s=="загрузка")return Uploading;
            if(s==Scheduling||s=="подготовка")return Scheduling;
            return Pending;
        }

        public static bool SkipAutoUpload(string state){
            string s=Normalize(state);
            return s==Scheduled||s==Unknown;
        }

        public static bool AllowRetry(string state){
            string s=Normalize(state);
            return s==Pending||s==Error||s==Uploading||s==Scheduling;
        }
    }
}
