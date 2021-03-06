﻿using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using StarryEyes.Casket.Cruds.Scaffolding;
using StarryEyes.Casket.DatabaseModels;

namespace StarryEyes.Casket.Cruds
{
    public class ManagementCrud : CrudBase<DatabaseManagement>
    {
        public ManagementCrud() : base(ResolutionMode.Replace) { }

        private const int AppVersion = 1;
        public string DatabaseVersion
        {
            get { return this.GetValue(AppVersion); }
            set { this.SetValue(AppVersion, value); }
        }

        #region synchronous crud

        private string GetValue(long id)
        {
            var mgmt = this.GetValueCore(id);
            return mgmt == null ? null : mgmt.Value;
        }

        private DatabaseManagement GetValueCore(long id)
        {
            using (this.AcquireReadLock())
            using (var con = this.DangerousOpenConnection())
            {
                return con.Query<DatabaseManagement>(
                    this.CreateSql("Id = @Id"), new { Id = id })
                          .SingleOrDefault();
            }
        }

        private void SetValue(long id, string value)
        {
            SetValueCore(new DatabaseManagement(id, value));
        }

        private void SetValueCore(DatabaseManagement mgmt)
        {
            using (this.AcquireWriteLock())
            using (var con = this.DangerousOpenConnection())
            using (var tr = con.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                con.Execute(this.TableInserter, mgmt);
                tr.Commit();
            }
        }

        #endregion

        internal async Task VacuumAsync()
        {
            // should execute WITHOUT transaction.
            await WriteTaskFactory.StartNew(() =>
            {
                using (AcquireWriteLock())
                using (var con = this.DangerousOpenConnection())
                {
                    con.Execute("VACUUM;");
                }
            });
        }
    }
}
