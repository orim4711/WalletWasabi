using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Wallets.PasswordFinder;

namespace WalletWasabi.Fluent.ViewModels.Login.PasswordFinder
{
	public partial class SearchPasswordViewModel : RoutableViewModel
	{
		[AutoNotify] private int _percentage;
		[AutoNotify] private int _remainingHour;
		[AutoNotify] private int _remainingMin;
		[AutoNotify] private int _remainingSec;
		[AutoNotify] private string _hourText;
		[AutoNotify] private string _minText;
		[AutoNotify] private string _secText;
		[AutoNotify] private bool _remainingTimeReceived;

		public SearchPasswordViewModel(PasswordFinderOptions options)
		{
			Title = "Password Finder";
			_hourText = "";
			_minText = "";
			_secText = "";
			CancelToken = new CancellationTokenSource();

			Task.Run(() =>
			{
				PasswordFinderHelper.TryFind(options, out var foundPassword, SetStatus, CancelToken.Token);
				Navigate().To(new PasswordFinderResultViewModel(foundPassword));
			});

			CancelCommand = ReactiveCommand.Create(() =>
			{
				CancelToken.Cancel();
				Navigate().Clear();
			});
		}

		private CancellationTokenSource CancelToken { get; }

		private void SetStatus(int percentage, TimeSpan remainingTime)
		{
			RemainingTimeReceived = true;
			Percentage = percentage;

			RemainingHour = remainingTime.Hours;
			RemainingMin = remainingTime.Minutes;
			RemainingSec = remainingTime.Seconds;

			HourText = $" hour{AddSIfPlural(RemainingHour)}{CloseSentenceIfZero(RemainingMin,RemainingSec)}";
			MinText = $" minute{AddSIfPlural(RemainingMin)}{CloseSentenceIfZero(RemainingSec)}";
			SecText = $" second{AddSIfPlural(RemainingSec)}.";
		}

		private static string AddSIfPlural(int n) => n > 1 ? "s" : "";

		private static string CloseSentenceIfZero(params int[] counts) => counts.All(x => x == 0) ? "." : " ";
	}
}
