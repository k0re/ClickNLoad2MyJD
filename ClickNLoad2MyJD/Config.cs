using System;
using System.IO;
using System.IO.IsolatedStorage;

namespace ClickNLoad2MyJD
{
    public static class Config
    {
        const string FILE_NAME = "ClickNLoad2MyJD.cfg";
        const string MYJD_MAIL = "MYJD_MAIL";
        const string MYJD_PASS = "MYJD_PASS";

        public static void DeleteConfiguration()
        {
            using (IsolatedStorageFile isoStore = IsolatedStorageFile.GetStore(IsolatedStorageScope.User | IsolatedStorageScope.Domain | IsolatedStorageScope.Assembly, null, null))
            {
                if (isoStore.FileExists(FILE_NAME))
                {
                    isoStore.DeleteFile(FILE_NAME);
                }
            }
        }

        public static (string Mail, string Password)? GetCredentials()
        {
            using (IsolatedStorageFile isoStore = IsolatedStorageFile.GetStore(IsolatedStorageScope.User | IsolatedStorageScope.Domain | IsolatedStorageScope.Assembly, null, null))
            {
                if (!isoStore.FileExists(FILE_NAME))
                {
                    return null;
                }

                (string Mail, string Password) credentials = (string.Empty, string.Empty);

                using (var configFile = isoStore.OpenFile(FILE_NAME, FileMode.Open))
                using (StreamReader streamReader = new StreamReader(configFile))
                {
                    var line = streamReader.ReadLine();
                    while (line != null)
                    {
                        var lineSplit = line.Split('=', 2);
                        if (lineSplit.Length > 1)
                        {
                            if (lineSplit[0].Equals(MYJD_MAIL, StringComparison.OrdinalIgnoreCase))
                            {
                                credentials.Mail = lineSplit[1];
                            }
                            else if (lineSplit[0].Equals(MYJD_PASS, StringComparison.OrdinalIgnoreCase))
                            {
                                credentials.Password = lineSplit[1];
                            }
                        }
                        line = streamReader.ReadLine();
                    }
                }

                if (string.IsNullOrWhiteSpace(credentials.Mail) || string.IsNullOrWhiteSpace(credentials.Password))
                {
                    return null;
                }

                return credentials;
            }
        }

        public static void SaveCredentials(string mail, string password)
        {
            using (IsolatedStorageFile isoStore = IsolatedStorageFile.GetStore(IsolatedStorageScope.User | IsolatedStorageScope.Domain | IsolatedStorageScope.Assembly, null, null))
            {
                using (var configFile = isoStore.OpenFile(FILE_NAME, FileMode.Create))
                using (StreamWriter streamWriter = new StreamWriter(configFile))
                {
                    streamWriter.WriteLine($"{MYJD_MAIL}={mail}");
                    streamWriter.WriteLine($"{MYJD_PASS}={password}");
                }
            }
        }
    }
}