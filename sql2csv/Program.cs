﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace sql2csv
{
	public class Program
	{
		public static ParallelOptions ParallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount / 2 };
		protected static readonly BlockingCollection<string> Queue = new BlockingCollection<string>();
		public static long InputRows;
		public static long OutputRows;

		public static int Main(string[] args)
		{
			var (query, output, connectionString) = ParseArgs(args);
			if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(output) || string.IsNullOrEmpty(connectionString))
			{
				PrintHelpMessage();
				return 1;
			}

			var global = Stopwatch.StartNew();

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
									var list = new List<string>();
									for (var i = 0; i < reader.FieldCount; i++)
									{
										var val = Regex.Replace(Regex.Replace((reader[i] ?? "").ToString().Trim(), "\\s+", " "), "\"", "\\\"");
										list.Add("\"" + val + "\"");
									}
									Queue.Add(string.Join(",", list));
									Interlocked.Increment(ref InputRows);
									if (InputRows % 1000 == 0)
									{
										Console.Write($"{InputRows:N0} in {timer.Elapsed}\r");
									}
								}
							}
							reader.Close();
						}
					}
				}
				Queue.CompleteAdding();
				Console.WriteLine();
				Console.WriteLine($"Got {InputRows:N0} rows from database in {timer.Elapsed}");
			});

			var write = Task.Run(() =>
			{
				var timer = Stopwatch.StartNew();
				using (var writer = new StreamWriter(output))
				{
					foreach (var row in Queue.GetConsumingEnumerable())
					{
						writer.WriteLine(row);
						Interlocked.Increment(ref OutputRows);
					}
				}
				Console.WriteLine($"Done writing {OutputRows:N0} in {timer.Elapsed}");
			});

			Task.WaitAll(read, write);
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
			var username = options.GetOrDefault("username", "");
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

			if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
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
