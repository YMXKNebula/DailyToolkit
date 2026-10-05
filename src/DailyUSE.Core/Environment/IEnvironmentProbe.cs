namespace DailyUSE.Core.Environment;

public interface IEnvironmentProbe
{
    MachineReport ReadBasic(DisplayInfo display);
    Task<MachineReport> ReadDetailsAsync(MachineReport basic, CancellationToken cancellationToken);
}
