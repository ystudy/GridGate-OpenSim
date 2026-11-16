// GridGate: records where each session starts, for the money service. See MERGING.md.
using OpenMetaverse;
using MySql.Data.MySqlClient;

namespace OpenSim.Data.MySQL
{
    public class MySQLSessionStartsData : MySQLGenericTableHandler<SessionStartRow>, ISessionStartsData
    {
        public MySQLSessionStartsData(string connectionString, string realm)
            : base(connectionString, realm, "GridGateSessionStarts") { }

        public void Record(UUID sessionID, UUID userID, UUID regionID, string source)
        {
            using (MySqlCommand cmd = new MySqlCommand())
            {
                cmd.CommandText = string.Format(
                    "insert into {0} (SessionID, UserID, RegionID, Source) values (?SessionID, ?UserID, ?RegionID, ?Source)", m_Realm);
                cmd.Parameters.AddWithValue("?SessionID", sessionID.ToString());
                cmd.Parameters.AddWithValue("?UserID", userID.ToString());
                cmd.Parameters.AddWithValue("?RegionID", regionID.ToString());
                cmd.Parameters.AddWithValue("?Source", source);
                ExecuteNonQuery(cmd);
            }
        }

        public void DeleteOld()
        {
            using (MySqlCommand cmd = new MySqlCommand())
            {
                cmd.CommandText = string.Format("delete from {0} where At < NOW() - INTERVAL 1 DAY", m_Realm);
                ExecuteNonQuery(cmd);
            }
        }
    }
}
