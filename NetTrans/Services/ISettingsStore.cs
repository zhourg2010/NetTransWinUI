using NetTrans.Models;

namespace NetTrans.Services;

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
