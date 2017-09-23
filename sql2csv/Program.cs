﻿using System;
using System.Collections.Concurrent;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace sql2csv
{
	public class Program
	{
		public static ParallelOptions ParallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount / 2, 4) };
		protected static readonly BlockingCollection<object[]> InputQueue = new BlockingCollection<object[]>();
		protected static readonly BlockingCollection<string> OutputQueue = new BlockingCollection<string>();
		public static long InputRows;
		public static long ProccessedRows;
		public static long OutputRows;
		public static TimeSpan InputTime;
		public static TimeSpan ProcessTime;
		public static TimeSpan OutputTime;


		public static int Main(string[] args)
		{
			var (query, output, connectionString) = ParseArgs(args);
#if DEBUG
			query = "SELECT EMail, Domain, cast(IsConfirmed as smallint), convert(varchar, AddDate, 120), cast(IsSend_System_JobRecommendation as smallint), cast(IsNeedConfirm_UkrNet as smallint) FROM EMailSource with (nolock) where email like '11malushka11@mail.ru74%'";
			output = "emailsource.csv";
			connectionString = "Data Source=beta.rabota.ua;Initial Catalog=RabotaUA2;Integrated Security=False;User ID=sa;Password=rabota;";
#endif
			if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(output) || string.IsNullOrEmpty(connectionString))
			{
				PrintHelpMessage();
				return 1;
			}

			var global = Stopwatch.StartNew();

			var status = new CancellationTokenSource();
			if (Environment.UserInteractive)
			{
				Task.Run(() =>
				{
					Console.WriteLine("Read -> Process -> Write");
					while (!status.IsCancellationRequested)
					{
						Console.Write($"{InputRows:N0} -> {ProccessedRows:N0} -> {OutputRows:N0} in {global.Elapsed}\r");
						Thread.Sleep(200);
					}
				}, status.Token);
			}

			var read = Task.Run(() =>
			{
				var timer = Stopwatch.StartNew();
				using (var connection = new SqlConnection(connectionString))
				{
					using (var command = new SqlCommand(query, connection) { CommandTimeout = 0 })
					{
						connection.Open();
						using (var reader = command.ExecuteReader())
						{
							if (reader.HasRows)
							{
								while (reader.Read())
								{
									var values = new object[reader.FieldCount];
									reader.GetValues(values);
									InputQueue.Add(values, status.Token);
									Interlocked.Increment(ref InputRows);
								}
							}
							reader.Close();
						}
					}
				}
				InputQueue.CompleteAdding();
				InputTime = timer.Elapsed;
			}, status.Token);

			var process = Task.Run(() =>
			{
				var timer = Stopwatch.StartNew();
				Parallel.ForEach(InputQueue.GetConsumingEnumerable(), ParallelOptions, values =>
				{
					var cells = values.Select(val =>
					{
						var str = (val ?? "").ToString().Trim();
						//str = HttpUtility.UrlDecode(str);
						str = Regex.Replace(str, @"[\u0000-\u001F]", string.Empty);
						str = Regex.Replace(str, "\\s+", " ");
						str = Regex.Replace(str, "\"", "\"\"");

						return "\"" + str + "\"";
					});
					OutputQueue.Add(string.Join(",", cells), status.Token);
					Interlocked.Increment(ref ProccessedRows);
				});
				OutputQueue.CompleteAdding();
				ProcessTime = timer.Elapsed;
			}, status.Token);

			var write = Task.Run(() =>
			{
				var timer = Stopwatch.StartNew();
				using (var writer = new StreamWriter(output, false, new UTF8Encoding(false)) { NewLine = "\n" })
				{
					foreach (var row in OutputQueue.GetConsumingEnumerable())
					{
						writer.WriteLine(row);
						Interlocked.Increment(ref OutputRows);
					}
				}
				OutputTime = timer.Elapsed;
			}, status.Token);

			Task.WaitAll(read, process, write);
			status.Cancel();
			Console.WriteLine();
			Console.WriteLine($"{InputRows:N0} input in {InputTime}");
			Console.WriteLine($"{ProccessedRows:N0} input in {ProcessTime}");
			Console.WriteLine($"{OutputRows:N0} input in {OutputTime}");
			Console.WriteLine($"Done in {global.Elapsed}");

			return 0;
		}

		private static (string query, string output, string connectionString) ParseArgs(string[] args)
		{
			var options = args.ToDictionary(arg => arg.TrimStart('-').Split('=').FirstOrDefault(), arg => arg.Split('=').LastOrDefault());

			var query = options.GetOrDefault("query", "");
			var input = options.GetOrDefault("input", "");
			var output = options.GetOrDefault("output", "");
			var server = options.GetOrDefault("server", "localhost");
			var database = options.GetOrDefault("database", "RabotaUA2");
			var username = options.GetOrDefault("username", "sa");
			var password = options.GetOrDefault("password", "");

			var hasValidInput = !string.IsNullOrEmpty(query) || !string.IsNullOrEmpty(input) && File.Exists(input);
			var hasValidOutput = !string.IsNullOrEmpty(output);

			if (!(hasValidInput && hasValidOutput))
			{
				return ("", "", "");
			}

			if (string.IsNullOrEmpty(query))
			{
				query = File.ReadAllText(input);
			}

			var builder = new SqlConnectionStringBuilder
			{
				DataSource = server,
				InitialCatalog = database,
				PersistSecurityInfo = true
			};

			if (string.IsNullOrEmpty(password))
			{
				builder.IntegratedSecurity = true;
			}
			else
			{
				builder.UserID = username;
				builder.Password = password;
			}

			return (query, output, builder.ConnectionString);
		}

		private static void PrintHelpMessage()
		{
			Console.WriteLine($"SQL2CSV Version: {Assembly.GetExecutingAssembly().GetName().Version}");
			Console.WriteLine("Required arguments:");
			Console.WriteLine();
			Console.WriteLine("  --query=\"select top 10 * from city\" - required if there is no input argument");
			Console.WriteLine("  --input=query.sql - required if there is no query argument");
			Console.WriteLine("  --output=city.csv");
			Console.WriteLine();
			Console.WriteLine("Optional arguments:");
			Console.WriteLine("  --server=localhost");
			Console.WriteLine("  --database=RabotaUA2");
			Console.WriteLine("  --username=sa");
			Console.WriteLine("  --password=password");
			Console.WriteLine();
			Console.WriteLine("Usage examples:");
			Console.WriteLine();
			Console.WriteLine("sql2csv --query=\"select top 10 * from city\" --output=city.csv");
			Console.WriteLine();
		}
	}
}
