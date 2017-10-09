﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Xml;

using DigitalPlatform.LibraryClient;
using DigitalPlatform.Text;
using DigitalPlatform.Xml;
using DigitalPlatform.IO;
using System.IO;

namespace DigitalPlatform.LibraryServer
{
    /// <summary>
    /// dp2library 服务器之间的复制 批处理任务
    /// </summary>
    public class ServerReplication : BatchTask
    {
        // 日志恢复级别
        public RecoverLevel RecoverLevel = RecoverLevel.Robust;

        string m_strUrl = "";
        string m_strUserName = "";
        string m_strPassword = "";

#if NO
        // 临时记忆断点信息，避免繁琐参数传入多层调用
        string m_strStartFileName = "";
        int m_nStartIndex = 0;
        string m_strStartOffset = "";

        string m_strWarningFileName = "";
#endif

        // 构造函数
        public ServerReplication(LibraryApplication app,
            string strName)
            : base(app, strName)
        {
            this.PerTime = 10 * 60 * 1000;	// 10 分钟

            this.Loop = true;
        }

        public override string DefaultName
        {
            get
            {
                return "服务器同步";
            }
        }

        // 解析 开始 参数
        static int ParseLogRecorverStart(string strStart,
            out long index,
            out string strFileName,
            out string strError)
        {
            strError = "";
            index = 0;
            strFileName = "";

            if (String.IsNullOrEmpty(strStart) == true)
                return 0;

            int nRet = strStart.IndexOf('@');
            if (nRet == -1)
            {
                try
                {
                    index = Convert.ToInt64(strStart);
                }
                catch (Exception)
                {
                    strError = "启动参数 '" + strStart + "' 格式错误：" + "如果没有@，则应为纯数字。";
                    return -1;
                }
                return 0;
            }

            try
            {
                index = Convert.ToInt64(strStart.Substring(0, nRet).Trim());
            }
            catch (Exception)
            {
                strError = "启动参数 '" + strStart + "' 格式错误：'" + strStart.Substring(0, nRet).Trim() + "' 部分应当为纯数字。";
                return -1;
            }

            strFileName = strStart.Substring(nRet + 1).Trim();

            // 如果文件名没有扩展名，自动加上
            if (String.IsNullOrEmpty(strFileName) == false)
            {
                nRet = strFileName.ToLower().LastIndexOf(".log");
                if (nRet == -1)
                    strFileName = strFileName + ".log";
            }

            return 0;
        }

        // 解析通用启动参数
        // 格式
        /*
         * <root recoverLevel='...' clearFirst='...' continueWhenError='...'/>
         * recoverLevel 缺省为 Snapshot
         * clearFirst 缺省为 false
         * continueWhenError 缺省值为 false
         * */
        public static int ParseLogRecoverParam(string strParam,
            out string strRecoverLevel,
            out bool bClearFirst,
            out bool bContinueWhenError,
            out string strError)
        {
            strError = "";
            bClearFirst = false;
            strRecoverLevel = "";
            bContinueWhenError = false;

            if (String.IsNullOrEmpty(strParam) == true)
                return 0;

            XmlDocument dom = new XmlDocument();

            try
            {
                dom.LoadXml(strParam);
            }
            catch (Exception ex)
            {
                strError = "strParam参数装入XML DOM时出错: " + ex.Message;
                return -1;
            }

            /*
            Logic = 0,  // 逻辑操作
            LogicAndSnapshot = 1,   // 逻辑操作，若失败则转用快照恢复
            Snapshot = 3,   // （完全的）快照
            Robust = 4,
             * */

            strRecoverLevel = dom.DocumentElement.GetAttribute("recoverLevel");
            string strClearFirst = dom.DocumentElement.GetAttribute("clearFirst");
            if (strClearFirst.ToLower() == "yes"
                || strClearFirst.ToLower() == "true")
                bClearFirst = true;
            else
                bClearFirst = false;

            bContinueWhenError = DomUtil.GetBooleanParam(dom.DocumentElement,
                "continueWhenError",
                false);

            return 0;
        }

        // 一次操作循环
        public override void Worker()
        {
            // 系统挂起的时候，不运行本线程
            if (this.App.ContainsHangup("LogRecover") == true)
                return;

            if (this.App.PauseBatchTask == true)
                return;

            string strError = "";
            int nRet = 0;

            // 获得源 dp2library 服务器配置信息
            {
                nRet = GetSourceServerCfg(out strError);
                if (nRet == -1)
                    goto ERROR1;

                nRet = CheckUID(out strError);
                if (nRet == -1)
                    goto ERROR1;
            }

            BatchTaskStartInfo startinfo = this.StartInfo;
            if (startinfo == null)
                startinfo = new BatchTaskStartInfo();   // 按照缺省值来

            long lStartIndex = 0;// 开始位置
            string strStartFileName = "";// 开始文件名
            nRet = ParseLogRecorverStart(startinfo.Start,
                out lStartIndex,
                out strStartFileName,
                out strError);
            if (nRet == -1)
            {
                this.AppendResultText("启动失败: " + strError + "\r\n");
                return;
            }

            //
            string strRecoverLevel = "";
            bool bClearFirst = false;
            bool bContinueWhenError = false;

            nRet = ParseLogRecoverParam(startinfo.Param,
                out strRecoverLevel,
                out bClearFirst,
                out bContinueWhenError,
                out strError);
            if (nRet == -1)
            {
                this.AppendResultText("启动失败: " + strError + "\r\n");
                return;
            }

            this.App.WriteErrorLog(this.Name + " 任务启动。");

#if NO
            // 当为容错恢复级别时，检查当前全部读者库的检索点是否符合要求
            if (this.RecoverLevel == LibraryServer.RecoverLevel.Robust)
            {
                // 检查全部读者库的检索途径，看是否满足都有“所借册条码号”这个检索途径的这个条件
                // return:
                //      -1  出错
                //      0   不满足
                //      1   满足
                nRet = this.App.DetectReaderDbFroms(out strError);
                if (nRet == -1)
                {
                    this.AppendResultText("检查读者库检索点时发生错误: " + strError + "\r\n");
                    return;
                }
                if (nRet == 0)
                {
                    this.AppendResultText("在容错恢复级别下，当前读者库中有部分或全部读者库缺乏“所借册条码号”检索点，无法进行日志恢复。请按照日志恢复要求，刷新所有读者库的检索点配置，然后再进行日志恢复\r\n");
                    return;
                }
            }
#endif

            // TODO: 检查当前是否有 重建检索点 的后台任务正在运行，或者还有没有运行完的部分。
            // 要求重建检索点的任务运行完以后才能执行日志恢复任务

#if NO
            if (bClearFirst == true)
            {
                nRet = this.App.ClearAllDbs(this.RmsChannels,
                    out strError);
                if (nRet == -1)
                {
                    this.AppendResultText("清除全部数据库记录时发生错误: " + strError + "\r\n");
                    return;
                }
            }
#endif

            // 进行处理
            BreakPointInfo breakpoint = null;

            if (string.IsNullOrEmpty(strStartFileName) == false
                && strStartFileName != "continue")
                breakpoint = new BreakPointInfo(strStartFileName.Substring(0, 8), lStartIndex);

            this.AppendResultText("*********\r\n");

            if (strStartFileName == "continue" || string.IsNullOrEmpty(strStartFileName))
            {
                // 按照断点信息处理
                this.AppendResultText("从上次断点位置继续\r\n");

                // return:
                //      -1  出错
                //      0   没有发现断点信息
                //      1   成功
                nRet = ReadBreakPoint(out breakpoint,
        out strError);
                if (nRet == -1)
                    goto ERROR1;
                if (nRet == 0)
                {
                    // return;
                    goto ERROR1;
                }
            }
            else
            {
                // 先从远端复制整个数据库，然后从开始复制时的日志末尾进行同步
                this.AppendResultText("指定的数据库\r\n");

                // 采纳先前创建好的复制并继续的断点信息
            }

            Debug.Assert(breakpoint != null, "");

            this.AppendResultText("计划进行的处理：\r\n---\r\n" + breakpoint.GetSummary() + "\r\n---\r\n\r\n");
            //if (this.StartInfos.Count > 0)
            //    this.AppendResultText("等待队列：\r\n---\r\n" + GetSummary(this.StartInfos) + "\r\n---\r\n\r\n");

            // m_nRecordCount = 0;

            // return:
            //      -1  出错
            //      0   中断
            //      1   完成
            nRet = ProcessOperLogs(breakpoint,
                bContinueWhenError,
                out strError);
            if (nRet == -1 || nRet == 0)
            {
                // 保存断点文件
                SaveBreakPoint(breakpoint, false);
                this.StartInfo = null;  // 迫使后面循环处理的时候，从断点位置继续
                goto ERROR1;
            }
#if NO
            bool bStart = false;
            if (String.IsNullOrEmpty(strStartFileName) == true)
            {
                // 做所有文件
                bStart = true;
            }


            // 列出所有日志文件
            DirectoryInfo di = new DirectoryInfo(this.App.OperLog.Directory);

            FileInfo[] fis = di.GetFiles("*.log");

            // BUG!!! 以前缺乏排序。2008/2/1
            Array.Sort(fis, new FileInfoCompare());

            for (int i = 0; i < fis.Length; i++)
            {
                if (this.Stopped == true)
                    break;

                string strFileName = fis[i].Name;

                this.AppendResultText("检查文件 " + strFileName + "\r\n");

                if (bStart == false)
                {
                    // 从特定文件开始做
                    if (string.CompareOrdinal(strStartFileName, strFileName) <= 0)  // 2015/9/12 从等号修改为 Compare
                    {
                        bStart = true;
                        if (lStartIndex < 0)
                            lStartIndex = 0;
                        // lStartIndex = Convert.ToInt64(startinfo.Param);
                    }
                }

                if (bStart == true)
                {
                    nRet = DoOneLogFile(strFileName,
                        lStartIndex,
                        bContinueWhenError,
                        out strError);
                    if (nRet == -1)
                        goto ERROR1;
                    lStartIndex = 0;    // 第一个文件以后的文件就全做了
                }

            }
#endif

            this.AppendResultText("循环结束\r\n");

            this.App.WriteErrorLog("日志恢复 任务结束。");

            // 保存断点文件
            SaveBreakPoint(breakpoint, false);
            this.StartInfo = null;  // 迫使后面循环处理的时候，从断点位置继续
            return;
        ERROR1:
            this.AppendResultText(strError + "\r\n");
            return;
        }

        // 获得源 dp2library 服务器配置信息
        int GetSourceServerCfg(out string strError)
        {
            strError = "";

            if (this.App.LibraryCfgDom == null
                || this.App.LibraryCfgDom.DocumentElement == null)
            {
                strError = "library.xml 尚未配置";
                return -1;
            }

            XmlElement server = this.App.LibraryCfgDom.DocumentElement.SelectSingleNode("serverReplication") as XmlElement;
            if (server == null)
            {
                strError = "library.xml 中尚未配置 serverReplication 元素";
                return -1;
            }

            this.m_strUrl = DomUtil.GetAttr(server, "url");
            if (string.IsNullOrEmpty(this.m_strUrl))
            {
                strError = "library.xml 中 serverReplication 元素尚未配置 url 属性";
                return -1;
            }
            this.m_strUserName = DomUtil.GetAttr(server, "username");
            if (string.IsNullOrEmpty(this.m_strUserName))
            {
                strError = "library.xml 中 serverReplication 元素尚未配置 username 属性";
                return -1;
            }

            try
            {
                this.m_strPassword = LibraryApplication.DecryptPassword(DomUtil.GetAttr(server, "password"));
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                strError = "library.xml 中 serverReplication 元素的 password 属性值不合法";
                return -1;
            }
            return 0;
        }

        // 检查主服务器和当前服务器(从服务器)之间 UID 是否相同
        // return:
        //      -1  出错
        //      0   没有问题
        //      1   UID 发生重复了
        int CheckUID(out string strError)
        {
            strError = "";

            LibraryChannel channel = new LibraryChannel();
            channel.Url = this.m_strUrl;

            channel.BeforeLogin += new BeforeLoginEventHandle(Channel_BeforeLogin);
            _stop.BeginLoop();
            try
            {
                string strUID = "";
                string strVersion = "";
                long lRet = channel.GetVersion(this._stop,
    out strVersion,
    out strUID,
    out strError);
                if (lRet == -1)
                {
                    strError = "获得主服务器 dp2library 的 UID 时出错: " + strError;
                    return -1;
                }

                if (this.App.UID == strUID)
                {
                    strError = "当前服务器 dp2library 和主服务器 '" + m_strUrl + "' 的 UID ('" + strUID + "') 重复了，放弃同步操作";
                    return -1;
                }

                return 0;
            }
            finally
            {
                _stop.EndLoop();
                channel.BeforeLogin -= new BeforeLoginEventHandle(Channel_BeforeLogin);
                channel.Close();
            }
        }

        // return:
        //      -1  出错
        //      0   中断
        //      1   完成
        int ProcessOperLogs(BreakPointInfo breakpoint,
            bool bContinueWhenError,
            out string strError)
        {
            strError = "";

            DateTime now = DateTime.Now;

            string strStartDate = breakpoint.Date;  // +":" + breakpoint.Offset.ToString();
            string strEndDate = DateTimeUtil.DateTimeToString8(now);
            List<string> filenames = null;
            string strWarning = "";
            // 根据日期范围，发生日志文件名
            // parameters:
            //      strStartDate    起始日期。8字符
            //      strEndDate  结束日期。8字符
            // return:
            //      -1  错误
            //      0   成功
            int nRet = OperLogLoader.MakeLogFileNames(strStartDate,
                strEndDate,
                true,  // true,
                out filenames,
                out strWarning,
                out strError);
            if (nRet == -1)
                return -1;

            if (String.IsNullOrEmpty(strWarning) == false)
            {
                // 可能有超过当天日期的被舍弃
            }

#if NO
            if (filenames.Count > 0 && string.IsNullOrEmpty(strEndRange) == false)
            {
                filenames[filenames.Count - 1] = filenames[filenames.Count - 1] + ":" + strEndRange;
            }
            if (filenames.Count > 0 && string.IsNullOrEmpty(strStartRange) == false)
            {
                filenames[0] = filenames[0] + ":" + strStartRange;
            }
#endif
            if (filenames.Count > 0 && breakpoint.Offset > 0)
                filenames[0] = filenames[0] + ":" + breakpoint.Offset.ToString();

            string strTempFileName = "";
            strTempFileName = this.App.GetTempFileName("attach");

            LibraryChannel channel = new LibraryChannel();
            channel.Url = this.m_strUrl;

            channel.BeforeLogin += new BeforeLoginEventHandle(Channel_BeforeLogin);
            _stop.BeginLoop();
            try
            {

                OperLogLoader loader = new OperLogLoader();
                loader.Channel = channel;
                loader.Stop = this._stop;
                // loader.estimate = estimate;
                loader.FileNames = filenames;
                loader.Level = 0;  //  0 完整级别
                loader.ReplicationLevel = true;
                loader.AutoCache = false;
                loader.CacheDir = "";
                // loader.Filter = "borrow,return,setReaderInfo,setBiblioInfo,setEntity,setOrder,setIssue,setComment,amerce,passgate,getRes";
                loader.LogType = LogType.OperLog;

                foreach (OperLogItem item in loader)
                {
                    if (this.Stopped)
                    {
                        strError = "用户中断";
                        return -1;
                    }
                    string date = item.Date;

                    // 处理
                    // this.AppendResultText("--" + date + "\r\n");
                    this.SetProgressText(date + ":" + item.Index.ToString());

                    Stream attachment = null;
                    try
                    {
                        if (item.AttachmentLength != 0)
                        {
                            // return:
                            //      -1  出错
                            //      0   没有找到日志记录
                            //      >0  附件总长度
                            long lRet = loader.DownloadAttachment(item,
                strTempFileName,
                out strError);
                            if (lRet == -1 || lRet == 0)
                            {
                                this.AppendResultText("*** 做日志记录 " + item.Date + " " + (item.Index).ToString() + " 时，下载附件部分发生错误：" + strError + "\r\n");

                                if (// this.RecoverLevel == RecoverLevel.Logic &&
                                    bContinueWhenError == false)
                                    return -1;
                                goto CONTINUE;
                            }
                            attachment = File.Open(strTempFileName, FileMode.Open);
                        }

                        nRet = this.DoOperLogRecord(
                            this.RecoverLevel,
                            item.Xml,
        attachment,
        out strError);
                        if (nRet == -1)
                        {
                            this.AppendResultText("*** 做日志记录 " + item.Date + " " + (item.Index).ToString() + " 时发生错误：" + strError + "\r\n");

                            // 2007/6/25
                            // 如果为纯逻辑恢复(并且 bContinueWhenError 为 false)，遇到错误就停下来。这便于进行测试。
                            // 若不想停下来，可以选择“逻辑+快照”型，或者设置 bContinueWhenError 为 true
                            if (// this.RecoverLevel == RecoverLevel.Logic &&
                                bContinueWhenError == false)
                                return -1;
                        }
                    }
                    finally
                    {
                        if (attachment != null)
                        {
                            attachment.Close();
                            attachment = null;
                        }
                    }

                CONTINUE:
                    breakpoint.Date = date;
                    breakpoint.Offset = item.Index;
                }

                return 1;
            }
            catch (InterruptException)
            {
                strError = "用户中断";
                return 0;
            }
            finally
            {
                if (File.Exists(strTempFileName))
                    File.Delete(strTempFileName);

                _stop.EndLoop();
                channel.BeforeLogin -= new BeforeLoginEventHandle(Channel_BeforeLogin);
                channel.Close();
            }
        }

        void Channel_BeforeLogin(object sender, BeforeLoginEventArgs e)
        {
            if (e.FirstTry == false)
            {
                e.Cancel = true;
                return;
            }

            e.UserName = m_strUserName;
            e.Password = m_strPassword;

            e.Parameters = "";
            e.Parameters += ",client=dp2library|" + LibraryApplication.Version;

            e.LibraryServerUrl = m_strUrl;
        }

        // 读出断点信息，和恢复 this.StartInfos
        // return:
        //      -1  出错
        //      0   没有发现断点信息
        //      1   成功
        int ReadBreakPoint(out BreakPointInfo breakpoint,
            out string strError)
        {
            strError = "";
            breakpoint = null;
            List<BatchTaskStartInfo> start_infos = null;

            string strText = "";
            // 从断点记忆文件中读出信息
            // return:
            //      -1  error
            //      0   file not found
            //      1   found
            int nRet = this.App.ReadBatchTaskBreakPointFile(this.DefaultName,
                out strText,
                out strError);
            if (nRet == -1)
                return -1;
            if (nRet == 0)
            {
                strError = "启动失败。因当前还没有断点信息，请指定为其他方式运行";
                return 0;
            }

            string strStartInfos = "";
            string strBreakPoint = "";
            StringUtil.ParseTwoPart(strText,
                "|||",
                out strBreakPoint,
                out strStartInfos);

            // 可能会抛出异常
            breakpoint = BreakPointInfo.Build(strBreakPoint);
            start_infos = FromString(strStartInfos);

            if (start_infos != null)
                this.StartInfos = start_infos;

            return 1;
        }

        // 保存断点信息，并保存 this.StartInfos
        void SaveBreakPoint(BreakPointInfo infos,
            bool bClearStartInfos)
        {
            // 写入断点文件
            this.App.WriteBatchTaskBreakPointFile(this.Name,
                infos.ToString() + "|||" + ToString(this.StartInfos));

            if (bClearStartInfos)
                this.StartInfos = new List<BatchTaskStartInfo>();   // 避免残余信息对后一轮运行发生影响
        }

        static List<BatchTaskStartInfo> FromString(string strText)
        {
            List<BatchTaskStartInfo> results = new List<BatchTaskStartInfo>();
            string[] segments = strText.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments)
            {
                BatchTaskStartInfo info = BatchTaskStartInfo.FromString(segment);
                results.Add(info);
            }

            return results;
        }

        static string ToString(List<BatchTaskStartInfo> start_infos)
        {
            StringBuilder text = new StringBuilder();
            foreach (BatchTaskStartInfo info in start_infos)
            {
                if (text.Length > 0)
                    text.Append(";");
                text.Append(info.ToString());
            }

            return text.ToString();
        }


        // 一个服务器的断点信息
        class BreakPointInfo
        {
            public string Date { get; set; }    // 日志文件日期
            public long Offset { get; set; }     // 偏移

            public BreakPointInfo()
            {
            }

            public BreakPointInfo(string strDate, long lOffset)
            {
                if (strDate.Length != 8)
                    throw new ArgumentException("strDate 参数值应为 8 字符", "strDate");

                this.Date = strDate;
                this.Offset = lOffset;
            }

            // 通过字符串构造
            public static BreakPointInfo Build(string strText)
            {
                Hashtable table = StringUtil.ParseParameters(strText);

                BreakPointInfo info = new BreakPointInfo();
                info.Date = (string)table["date"];

                if (info.Date.Length != 8)
                    throw new ArgumentException("strText 中 date 参数值应为 8 字符", "strText");

                info.Offset = Convert.ToInt64((string)table["offset"]);
                return info;
            }

            // 变换为字符串
            public override string ToString()
            {
                Hashtable table = new Hashtable();
                table["date"] = this.Date;
                table["offset"] = this.Offset.ToString();
                return StringUtil.BuildParameterString(table);
            }

            // 小结文字
            public string GetSummary()
            {
                string strResult = "";
                strResult += "日期: " + this.Date;
                strResult += "偏移: " + this.Offset.ToString();
                return strResult;
            }
        }
    }
}