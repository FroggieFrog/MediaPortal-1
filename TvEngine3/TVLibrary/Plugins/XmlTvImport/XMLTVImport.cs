#region Copyright (C) 2005-2011 Team MediaPortal

// Copyright (C) 2005-2011 Team MediaPortal
// http://www.team-mediaportal.com
// 
// MediaPortal is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 2 of the License, or
// (at your option) any later version.
// 
// MediaPortal is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with MediaPortal. If not, see <http://www.gnu.org/licenses/>.

#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Text.RegularExpressions;
using System.Xml;
using TvDatabase;
using TvLibrary.Log;
using Gentle.Framework;

namespace TvEngine
{
  internal class XMLTVImport : IComparer
  {
    public delegate void ShowProgressHandler(Stats stats);

    public event ShowProgressHandler ShowProgress;

    private class ChannelPrograms
    {
      public string Name;
      public string ExternalId;
      public ProgramList programs = new ProgramList();
    };

    public class Stats
    {
      private string _status = "";
      private int _programs = 0;
      private int _channels = 0;
      private DateTime _startTime = DateTime.Now;
      private DateTime _endTime = DateTime.Now;

      public string Status
      {
        get { return _status; }
        set { _status = value; }
      }

      public int Programs
      {
        get { return _programs; }
        set { _programs = value; }
      }

      public int Channels
      {
        get { return _channels; }
        set { _channels = value; }
      }

      public DateTime StartTime
      {
        get { return _startTime; }
        set { _startTime = value; }
      }

      public DateTime EndTime
      {
        get { return _endTime; }
        set { _endTime = value; }
      }
    };

    private string _errorMessage = "";
    private Stats _status = new Stats();
    private TvBusinessLayer layer = new TvBusinessLayer();

    private static bool _isImporting = false;
    private bool _skipTextProcessing;

    public XMLTVImport()
    {
    }

    public string ErrorMessage
    {
      get { return _errorMessage; }
    }

    public Stats ImportStats
    {
      get { return _status; }
    }

    private int ParseStarRating(string epgRating)
    {
      int Rating = -1;
      try
      {
        // format = 5.2/10
        // check if the epgRating is within a xml tag			
        epgRating = epgRating.Trim();
        if (string.IsNullOrEmpty(epgRating))
          return Rating;

        if (epgRating.StartsWith("<"))
        {
          int endStartTagIdx = epgRating.IndexOf(">") + 1;
          int length = epgRating.IndexOf("</", endStartTagIdx) - endStartTagIdx;
          epgRating = epgRating.Substring(endStartTagIdx, length);
        }
        string strRating = epgRating;
        int slashPos = strRating.IndexOf('/');
        // Some EPG providers only supply the value without n/10
        if (slashPos > 0)
          strRating = strRating.Remove(slashPos);

        decimal tmpRating = -1;
        NumberFormatInfo NFO = NumberFormatInfo.InvariantInfo;
        NumberStyles NStyle = NumberStyles.Float;

        if (Decimal.TryParse(strRating, NStyle, NFO, out tmpRating))
          Rating = Convert.ToInt16(tmpRating);
        else
          Log.Info("XMLTVImport: star-rating could not be used - {0},({1})", epgRating, strRating);
      }
      catch (Exception ex)
      {
        Log.Error("XMLTVImport: Error parsing star-rating - {0}", epgRating, ex.Message);
      }
      return Rating;
    }

    public bool Import(string fileName, bool deleteBeforeImport, bool showProgress)
    {
      _errorMessage = "";
      if (_isImporting == true)
      {
        _errorMessage = "already importing...";
        return false;
      }
      _isImporting = true;

      bool result = false;

      // remove old programs
      _status.Status = "Removing old programs";
      _status.Channels = 0;
      _status.Programs = 0;
      _status.StartTime = DateTime.Now;
      _status.EndTime = new DateTime(1971, 11, 6);
      if (showProgress && ShowProgress != null) ShowProgress(_status);

      layer.RemoveOldPrograms();

      /*
      // for each channel, get the last program's time
      Dictionary<int, DateTime> lastProgramForChannel = new Dictionary<int, DateTime>();
      IList channels = Channel.ListAll();
      foreach (Channel ch in channels)
      {
        SqlBuilder sb = new SqlBuilder(StatementType.Select, typeof(TvDatabase.Program));
        sb.AddConstraint(Operator.Equals, "idChannel", ch.IdChannel);
        sb.AddOrderByField(false, "starttime");
        sb.SetRowLimit(1);
        SqlStatement stmt = sb.GetStatement(true);
        IList programsInDbs = ObjectFactory.GetCollection(typeof(TvDatabase.Program), stmt.Execute());

        DateTime lastProgram = DateTime.MinValue;
        if (programsInDbs.Count > 0)
        {
          TvDatabase.IProgram p = (TvDatabase.Program)programsInDbs[0];
          lastProgram = p.EndTime;
        }
        lastProgramForChannel[ch.IdChannel] = lastProgram;
      }*/

      //TVDatabase.SupressEvents = true;
      bool useTimeZone = layer.GetSetting("xmlTvUseTimeZone", "false").Value == "true";
      int hours;
      if (!Int32.TryParse(layer.GetSetting("xmlTvTimeZoneHours", "0").Value, out hours))
      {
        Log.Error("xmltv: xmlTvTimeZoneHours not a valid integer: {0}", layer.GetSetting("xmlTvTimeZoneHours", "0").Value);
        hours = 0;
      };
      int mins;
      if (!Int32.TryParse(layer.GetSetting("xmlTvTimeZoneMins", "0").Value, out mins))
      {
        Log.Error("xmltv: xmlTvTimeZoneMins not a valid integer: {0}", layer.GetSetting("xmlTvTimeZoneMins", "0").Value);
        mins = 0;
      }
      int timeZoneCorrection = hours * 60 + mins;
      _skipTextProcessing = layer.GetSetting("xmlTvNoTextMod", "false").Value == "true";

      List<ChannelPrograms> Programs = new List<ChannelPrograms>();
      Dictionary<int, ChannelPrograms> dChannelPrograms = new Dictionary<int, ChannelPrograms>();
      try
      {
        Log.WriteFile("xmltv import {0}", fileName);

        //
        // Make sure the file exists before we try to do any processing
        //
        if (File.Exists(fileName))
        {
          _status.Status = "Loading channel list";
          _status.Channels = 0;
          _status.Programs = 0;
          _status.StartTime = DateTime.Now;
          _status.EndTime = new DateTime(1971, 11, 6);
          if (showProgress && ShowProgress != null) ShowProgress(_status);

          IList<Channel> allChannels = Channel.ListAll();

          int iChannel = 0;
          using (XmlReader xmlReader = XmlReader.Create(fileName, new XmlReaderSettings() { DtdProcessing = DtdProcessing.Parse }))
          {
            #region import non-mapped channels by their display-name

            if (xmlReader.ReadToDescendant("tv"))
            {
              // get the first channel
              if (xmlReader.ReadToDescendant("channel"))
              {
                do
                {
                  String id = xmlReader.GetAttribute("id");
                  if (id == null || id.Length == 0)
                  {
                    Log.Error("  channel#{0} doesnt contain an id", iChannel);
                  }
                  else
                  {
                    String displayName = null;

                    using (XmlReader xmlChannel = xmlReader.ReadSubtree())
                    {
                      xmlChannel.ReadStartElement(); // read channel
                                                     // now, xmlChannel is positioned on the first sub-element of <channel>
                      while (!xmlChannel.EOF)
                      {
                        if (xmlChannel.NodeType == XmlNodeType.Element)
                        {
                          switch (xmlChannel.Name)
                          {
                            case "display-name":
                            case "Display-Name":
                              if (displayName == null) displayName = xmlChannel.ReadString();
                              else xmlChannel.Skip();
                              break;
                            // could read more stuff here, like icon...
                            default:
                              // unknown, skip entire node
                              xmlChannel.Skip();
                              break;
                          }
                        }
                        else
                          xmlChannel.Read();
                      }
                    }

                    if (displayName == null || displayName.Length == 0)
                    {
                      Log.Error("  channel#{0} xmlid:{1} doesnt contain an displayname", iChannel, id);
                    }
                    else
                    {
                      Dictionary<int, Channel> guideChannels = new Dictionary<int, Channel>();
                      // a guide channel can be mapped to multiple tvchannels
                      foreach (Channel chan in allChannels)
                      {
                        if (chan.ExternalId == id)
                        {
                          ChannelPrograms newProgChan = new ChannelPrograms();
                          newProgChan.Name = chan.DisplayName;
                          newProgChan.ExternalId = chan.ExternalId;
                          Programs.Add(newProgChan);

                          Log.WriteFile("  channel#{0} xmlid:{1} name:{2} dbsid:{3}", iChannel, chan.ExternalId,
                                        chan.DisplayName, chan.IdChannel);
                          if (!guideChannels.ContainsKey(chan.IdChannel))
                          {
                            guideChannels.Add(chan.IdChannel, chan);
                            dChannelPrograms.Add(chan.IdChannel, newProgChan);
                          }
                        }
                      }

                      _status.Channels++;
                      if (showProgress && ShowProgress != null) ShowProgress(_status);
                    }
                  }
                  iChannel++;
                  // get the next channel
                } while (xmlReader.ReadToNextSibling("channel"));
              }
            }

            #endregion
          }

          SqlBuilder sb = new SqlBuilder(StatementType.Select, typeof(Channel));
          sb.AddOrderByField(true, "externalId");
          sb.AddConstraint("visibleInGuide = 1");
          sb.AddConstraint("externalId IS NOT null");
          sb.AddConstraint(Operator.NotEquals, "externalId", "");

          SqlStatement stmt = sb.GetStatement(true);
          allChannels = ObjectFactory.GetCollection<Channel>(stmt.Execute());
          if (allChannels.Count == 0)
          {
            _isImporting = false;
            return true;
          }

          ///////////////////////////////////////////////////////////////////////////
          /*  design:
           * 1. create a Dictionary<string,Channel> using the externalid as the key,
           *    add all channels to this Dictionary 
           *    Note: channel -> guidechannel is a one-to-many relationship. 
           * 2. Read all programs from the xml file
           * 3. Create a program for each mapped channel
           */
          ///////////////////////////////////////////////////////////////////////////
          Dictionary<string, List<Channel>> allChannelMappingsByExternalId = new Dictionary<string, List<Channel>>();

          string previousExternalId = null;
          // one-to-many so we need a collection of channels for each externalId
          List<Channel> eidMappedChannels = new List<Channel>();

          for (int i = 0; i < allChannels.Count; i++)
          {
            Channel ch = allChannels[i];

            if (previousExternalId == null)
            {
              eidMappedChannels.Add(ch);
              previousExternalId = ch.ExternalId;
            }
            else if (ch.ExternalId == previousExternalId)
            {
              eidMappedChannels.Add(ch);
            }
            else
            {
              // got all channels for this externalId. Add the mappings
              allChannelMappingsByExternalId.Add(previousExternalId, eidMappedChannels);
              // new externalid, create a new List & add the channel to the new List
              eidMappedChannels = new List<Channel>();
              eidMappedChannels.Add(ch);
              previousExternalId = ch.ExternalId;
            }

            if (i == allChannels.Count - 1)
            {
              allChannelMappingsByExternalId.Add(previousExternalId, eidMappedChannels);
            }
          }

          int programIndex = 0;
          _status.Status = "Loading TV programs";
          if (showProgress && ShowProgress != null) ShowProgress(_status);

          Log.Debug("xmltvimport: Reading TV programs");
          using (XmlReader xmlReader = XmlReader.Create(fileName, new XmlReaderSettings() { DtdProcessing = DtdProcessing.Parse }))
          {
            if (xmlReader.ReadToDescendant("tv"))
            {
              // get the first programme
              if (xmlReader.ReadToDescendant("programme"))
              {
                do
                {
                  ChannelPrograms channelPrograms = new ChannelPrograms();

                  String nodeStart = xmlReader.GetAttribute("start");
                  String nodeStop = xmlReader.GetAttribute("stop");
                  String nodeChannel = xmlReader.GetAttribute("channel");
                  if (allChannelMappingsByExternalId.ContainsKey(nodeChannel))
                  {

                    String nodeTitle = null;
                    String nodeCategory = null;
                    String nodeDescription = null;
                    String nodeEpisode = null;
                    String nodeRepeat = null;
                    String nodeEpisodeNum = null;
                    String nodeEpisodeNumSystem = null;
                    String nodeDate = null;
                    String nodeStarRating = null;
                    String nodeClassification = null;

                    #region read programme node
                    using (XmlReader xmlProg = xmlReader.ReadSubtree())
                    {
                      xmlProg.ReadStartElement(); // read programme
                                                  // now, xmlProg is positioned on the first sub-element of <programme>
                      while (!xmlProg.EOF)
                      {
                        if (xmlProg.NodeType == XmlNodeType.Element)
                        {
                          switch (xmlProg.Name)
                          {
                            case "title":
                              if (nodeTitle == null) nodeTitle = xmlProg.ReadString();
                              else xmlProg.Skip();
                              break;
                            case "category":
                              if (nodeCategory == null) nodeCategory = xmlProg.ReadString();
                              else xmlProg.Skip();
                              break;
                            case "desc":
                              if (nodeDescription == null) nodeDescription = xmlProg.ReadString();
                              else xmlProg.Skip();
                              break;
                            case "sub-title":
                              if (nodeEpisode == null) nodeEpisode = xmlProg.ReadString();
                              else xmlProg.Skip();
                              break;
                            case "previously-shown":
                              if (nodeRepeat == null) nodeRepeat = xmlProg.ReadString();
                              else xmlProg.Skip();
                              break;
                            case "episode-num":
                              if (nodeEpisodeNum == null)
                              {
                                nodeEpisodeNumSystem = xmlProg.GetAttribute("system");
                                nodeEpisodeNum = xmlProg.ReadString();
                              }
                              else xmlProg.Skip();
                              break;
                            case "date":
                              if (nodeDate == null) nodeDate = xmlProg.ReadString();
                              else xmlProg.Skip();
                              break;
                            case "star-rating":
                              if (nodeStarRating == null) nodeStarRating = xmlProg.ReadInnerXml();
                              else xmlProg.Skip();
                              break;
                            case "rating":
                              if (nodeClassification == null) nodeClassification = xmlProg.ReadInnerXml();
                              else xmlProg.Skip();
                              break;
                            default:
                              // unknown, skip entire node
                              xmlProg.Skip();
                              break;
                          }
                        }
                        else
                          xmlProg.Read();
                      }
                    }
                    #endregion

                    if (nodeStart != null && nodeChannel != null && nodeTitle != null &&
                        nodeStart.Length > 0 && nodeChannel.Length > 0 && nodeTitle.Length > 0)
                    {
                      #region verify/convert values (programme)
                      string description = "";
                      string category = "-";
                      string date = "";
                      string seriesNum = "";
                      string episodeNum = "";
                      string episodeName = "";
                      string episodePart = "";
                      int starRating = -1;
                      string classification = "";

                      string title = ConvertHTMLToAnsi(nodeTitle);

                      long startDate = 0;
                      if (nodeStart.Length >= 14)
                      {
                        if (Char.IsDigit(nodeStart[12]) && Char.IsDigit(nodeStart[13]))
                          startDate = Int64.Parse(nodeStart.Substring(0, 14)); //20040331222000
                        else
                          startDate = 100 * Int64.Parse(nodeStart.Substring(0, 12)); //200403312220
                      }
                      else if (nodeStart.Length >= 12)
                      {
                        startDate = 100 * Int64.Parse(nodeStart.Substring(0, 12)); //200403312220
                      }

                      bool hasStopDate = false;
                      long stopDate = startDate;
                      if (nodeStop != null)
                      {
                        if (nodeStop.Length >= 14)
                        {
                          if (Char.IsDigit(nodeStop[12]) && Char.IsDigit(nodeStop[13]))
                            stopDate = Int64.Parse(nodeStop.Substring(0, 14)); //20040331222000
                          else
                            stopDate = 100 * Int64.Parse(nodeStop.Substring(0, 12)); //200403312220
                          hasStopDate = true;
                        }
                        else if (nodeStop.Length >= 12)
                        {
                          stopDate = 100 * Int64.Parse(nodeStop.Substring(0, 12)); //200403312220
                          hasStopDate = true;
                        }
                      }

                      startDate = CorrectIllegalDateTime(startDate);
                      stopDate = CorrectIllegalDateTime(stopDate);
                      string timeZoneStart = "";
                      string timeZoneEnd = "";
                      if (nodeStart.Length > 14)
                      {
                        timeZoneStart = nodeStart.Substring(14);
                        timeZoneStart = timeZoneStart.Trim();
                        timeZoneEnd = timeZoneStart;
                      }
                      if (nodeStop != null)
                      {
                        if (nodeStop.Length > 14)
                        {
                          timeZoneEnd = nodeStop.Substring(14);
                          timeZoneEnd = timeZoneEnd.Trim();
                        }
                      }

                      //
                      // add time correction
                      //

                      // correct program starttime
                      DateTime dateTimeStart = longtodate(startDate);
                      dateTimeStart = dateTimeStart.AddMinutes(timeZoneCorrection);

                      if (useTimeZone)
                      {
                        int off = GetTimeOffset(timeZoneStart);
                        int h = off / 100; // 220 -> 2,  -220 -> -2
                        int m = off - (h * 100); // 220 -> 20, -220 -> -20

                        dateTimeStart = dateTimeStart.AddHours(-h);
                        dateTimeStart = dateTimeStart.AddMinutes(-m);
                        dateTimeStart = dateTimeStart.ToLocalTime();
                      }
                      startDate = datetolong(dateTimeStart);

                      if (nodeStop != null)
                      {
                        // correct program endtime
                        DateTime dateTimeEnd = longtodate(stopDate);
                        dateTimeEnd = dateTimeEnd.AddMinutes(timeZoneCorrection);

                        if (useTimeZone)
                        {
                          int off = GetTimeOffset(timeZoneEnd);
                          int h = off / 100; // 220 -> 2,  -220 -> -2
                          int m = off - (h * 100); // 220 -> 20, -220 -> -20

                          dateTimeEnd = dateTimeEnd.AddHours(-h);
                          dateTimeEnd = dateTimeEnd.AddMinutes(-m);
                          dateTimeEnd = dateTimeEnd.ToLocalTime();
                        }
                        stopDate = datetolong(dateTimeEnd);
                      }
                      else stopDate = startDate;

                      if (nodeCategory != null)
                        category = nodeCategory;

                      if (nodeDescription != null)
                      {
                        description = ConvertHTMLToAnsi(nodeDescription);
                      }
                      if (nodeEpisode != null)
                      {
                        episodeName = ConvertHTMLToAnsi(nodeEpisode);
                        if (title.Length == 0)
                          title = nodeEpisode;
                      }

                      if (nodeEpisodeNum != null)
                      {
                        if (nodeEpisodeNumSystem != null)
                        {
                          // http://xml.coverpages.org/XMLTV-DTD-20021210.html
                          if (nodeEpisodeNumSystem == "xmltv_ns")
                          {
                            string serEpNum = ConvertHTMLToAnsi(nodeEpisodeNum.Replace(" ", ""));
                            int dot1 = serEpNum.IndexOf(".", 0);
                            int dot2 = serEpNum.IndexOf(".", dot1 + 1);
                            seriesNum = serEpNum.Substring(0, dot1);
                            episodeNum = serEpNum.Substring(dot1 + 1, dot2 - (dot1 + 1));
                            episodePart = serEpNum.Substring(dot2 + 1, serEpNum.Length - (dot2 + 1));
                            //xmltv_ns is theorically zero-based number will be increased by one
                            seriesNum = CorrectEpisodeNum(seriesNum, 1);
                            episodeNum = CorrectEpisodeNum(episodeNum, 1);
                            episodePart = CorrectEpisodeNum(episodePart, 1);
                          }
                          else if (nodeEpisodeNumSystem == "onscreen")
                          {
                            // example: 'S1 E10', 'E10'
                            Match m = Regex.Match(nodeEpisodeNum, @"(?:S(?<season>\d+))?\s*E(?<episode>\d+)");
                            if (m.Success)
                            {
                              if (m.Groups["season"].Success)
                              {
                                seriesNum = m.Groups["season"].Value;
                              }
                              episodeNum = m.Groups["episode"].Value;
                            }
                            else
                            {
                              // example: 'Episode #FFEE' 
                              string serEpNum = ConvertHTMLToAnsi(nodeEpisodeNum);
                              int num1 = serEpNum.IndexOf("#", 0);
                              if (num1 < 0) num1 = serEpNum.IndexOf("S", 0);
                              episodeNum = CorrectEpisodeNum(serEpNum.Substring(num1 + 1), 0);
                            }
                          }
                        }
                        else
                        // fixing mantis bug 1486: XMLTV import doesn't take episode number from TVGuide.xml made by WebEPG 
                        {
                          // example: '5' like WebEPG is creating
                          string serEpNum = ConvertHTMLToAnsi(nodeEpisodeNum.Replace(" ", ""));
                          episodeNum = CorrectEpisodeNum(serEpNum, 0);
                        }
                      }

                      if (nodeDate != null)
                      {
                        date = nodeDate;
                      }

                      if (nodeStarRating != null)
                      {
                        starRating = ParseStarRating(nodeStarRating);
                      }

                      if (nodeClassification != null)
                      {
                        classification = nodeClassification;
                      }

                      if (showProgress && ShowProgress != null && (_status.Programs % 100) == 0) ShowProgress(_status);

                      #endregion

                      #region create a program for every mapped channel

                      List<Channel> mappedChannels;

                      mappedChannels = allChannelMappingsByExternalId[nodeChannel];
                      if (mappedChannels != null && mappedChannels.Count > 0)
                      {
                        foreach (Channel chan in mappedChannels)
                        {
                          // get the channel program
                          channelPrograms = dChannelPrograms[chan.IdChannel];

                          // skip invalid programs with same start/end date
                          if (chan.IdChannel < 0 || hasStopDate && startDate == stopDate)
                          {
                            continue;
                          }

                          title = ProcessText(title);
                          description = ProcessText(description);
                          episodeName = ProcessText(episodeName);

                          Program prog = new Program(chan.IdChannel, longtodate(startDate), longtodate(stopDate), title,
                                                     description, category, Program.ProgramState.None,
                                                     System.Data.SqlTypes.SqlDateTime.MinValue.Value, seriesNum,
                                                     episodeNum, episodeName, episodePart, starRating, classification, -1);
                          channelPrograms.programs.Add(prog);
                          programIndex++;
                          _status.Programs++;
                        }
                      }
                      #endregion
                    }
                  }
                  // get the next programme
                } while (xmlReader.ReadToNextSibling("programme"));


                #region sort & remove invalid programs. Save all valid programs

                Log.Debug("xmltvimport: Sorting TV programs");

                _status.Programs = 0;
                _status.Status = "Sorting TV programs";
                if (showProgress && ShowProgress != null) ShowProgress(_status);
                DateTime dtStartDate = DateTime.Now.Date;
                //dtStartDate=dtStartDate.AddDays(-4);

                foreach (ChannelPrograms progChan in Programs)
                {
                  // empty, skip it
                  if (progChan.programs.Count == 0) continue;

                  progChan.programs.Sort();
                  progChan.programs.AlreadySorted = true;
                  progChan.programs.FixEndTimes();
                  progChan.programs.RemoveOverlappingPrograms(); // be sure that we do not have any overlapping

                  // get the id of the channel, just get the IdChannel of the first program
                  int idChannel = progChan.programs[0].IdChannel;

                  if (!deleteBeforeImport)
                  {
                    // retrieve all programs for this channel
                    SqlBuilder sb2 = new SqlBuilder(StatementType.Select, typeof(Program));
                    sb2.AddConstraint(Operator.Equals, "idChannel", idChannel);
                    sb2.AddOrderByField(false, "starttime");
                    SqlStatement stmt2 = sb2.GetStatement(true);
                    ProgramList dbPrograms = new ProgramList();
                    ObjectFactory.GetCollection<Program>(stmt2.Execute(), dbPrograms);
                    progChan.programs.RemoveOverlappingPrograms(dbPrograms, true);
                  }

                  for (int i = 0; i < progChan.programs.Count; ++i)
                  {
                    Program prog = progChan.programs[i];
                    // don't import programs which have already ended...
                    if (prog.EndTime <= dtStartDate)
                    {
                      progChan.programs.RemoveAt(i);
                      i--;
                      continue;
                    }

                    DateTime start = prog.StartTime;
                    DateTime end = prog.EndTime;
                    DateTime airDate = System.Data.SqlTypes.SqlDateTime.MinValue.Value;
                    try
                    {
                      airDate = prog.OriginalAirDate;
                      if (airDate > System.Data.SqlTypes.SqlDateTime.MinValue.Value &&
                          airDate < System.Data.SqlTypes.SqlDateTime.MaxValue.Value)
                        prog.OriginalAirDate = airDate;
                    }
                    catch (Exception)
                    {
                      Log.Info("XMLTVImport: Invalid year for OnAirDate - {0}", prog.OriginalAirDate);
                    }

                    if (prog.StartTime < _status.StartTime)
                      _status.StartTime = prog.StartTime;
                    if (prog.EndTime > _status.EndTime)
                      _status.EndTime = prog.EndTime;
                    _status.Programs++;
                    if (showProgress && ShowProgress != null && (_status.Programs % 100) == 0) ShowProgress(_status);
                  }
                  Log.Info("XMLTVImport: Inserting {0} programs for {1}", progChan.programs.Count.ToString(),
                           progChan.Name);
                  layer.InsertPrograms(progChan.programs,
                                       deleteBeforeImport
                                         ? DeleteBeforeImportOption.OverlappingPrograms
                                         : DeleteBeforeImportOption.None, ThreadPriority.BelowNormal);
                }
              }

              #endregion
              if (programIndex > 0)
              {
                _errorMessage = "File imported successfully";
                result = true;
              }
              else
                _errorMessage = "No programs found";
            }
          }
        }
        else
        {
          _errorMessage = "No xmltv file found";
          _status.Status = _errorMessage;
          Log.Error("xmltv data file was not found");
        }
      }
      catch (Exception ex)
      {
        _errorMessage = String.Format("Invalid XML file:{0}", ex.Message);
        _status.Status = String.Format("invalid XML file:{0}", ex.Message);
        Log.Error("XML tv import error loading {0} err:{1} \n {2}", fileName, ex.Message, ex.StackTrace);

        //TVDatabase.RollbackTransaction();
      }

      _isImporting = false;
      //      TVDatabase.SupressEvents = false;
      return result;
    }

    /// <summary>
    /// Removes newlines and duplicated spaces from given <paramref name="text"/>. This method only modifies content if
    /// setting "xmlTvNoTextMod" is <c>false</c>.
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    protected string ProcessText(string text)
    {
      if (_skipTextProcessing)
        return text;
      text = text.Replace("\r\n", " ");
      text = text.Replace("\n\r", " ");
      text = text.Replace("\r", " ");
      text = text.Replace("\n", " ");
      text = text.Replace("  ", " ");
      return text;
    }

    /// <summary>
    /// Parse and correct ep. # in the episode string
    /// </summary>
    /// <param name="episodenum"></param>
    /// <param name="nodeEpisodeNumSystemBase">int to add to the parsed episode num (depends on 0-based or not xmltv files)</param>
    /// <returns></returns>
    private string CorrectEpisodeNum(string episodenum, int nodeEpisodeNumSystemBase)
    {
      if (episodenum == "")
        return episodenum;

      // Find format of the episode number
      int slashpos = episodenum.IndexOf("/", 0);
      if (slashpos == -1)
      {
        // No slash found => assume it's just a plain number
        try
        {
          int epnum = Convert.ToInt32(episodenum);
          return Convert.ToString(epnum + nodeEpisodeNumSystemBase);
        }
        catch (Exception)
        {
          Log.WriteFile("XMLTVImport::CorrectEpisodeNum, could not parse '{0}' as plain number", episodenum);
        }
      }
      else
      {
        try
        {
          // Slash found -> assume it's formatted as <episode number>/<episodes>
          int epnum = Convert.ToInt32(episodenum.Substring(0, slashpos));
          int epcount = Convert.ToInt32(episodenum.Substring(slashpos + 1));
          return Convert.ToString(epnum + nodeEpisodeNumSystemBase) + "/" + Convert.ToString(epcount);
        }
        catch (Exception)
        {
          Log.WriteFile("XMLTVImport::CorrectEpisodeNum, could not parse '{0}' as episode/episodes", episodenum);
        }
      }
      return "";
    }

    private int GetTimeOffset(string timeZone)
    {
      // timezone can b in format:
      // GMT +0100 or GMT -0500
      // or just +0300
      if (timeZone.Length == 0) return 0;
      timeZone = timeZone.ToLowerInvariant();

      // just ignore GMT offsets, since we're calculating everything from GMT anyway
      if (timeZone.IndexOf("gmt") >= 0)
      {
        int ipos = timeZone.IndexOf("gmt");
        timeZone = timeZone.Substring(ipos + "GMT".Length);
      }

      timeZone = timeZone.Trim();
      if (timeZone[0] == '+' || timeZone[0] == '-')
      {
        string strOff = timeZone.Substring(1);
        try
        {
          int iOff = Int32.Parse(strOff);
          if (timeZone[0] == '-') return -iOff;
          else return iOff;
        }
        catch (Exception) { }
      }
      return 0;
    }

    private long CorrectIllegalDateTime(long datetime)
    {
      //format : 20050710245500
      long orgDateTime = datetime;
      long sec = datetime % 100;
      datetime /= 100;
      long min = datetime % 100;
      datetime /= 100;
      long hour = datetime % 100;
      datetime /= 100;
      long day = datetime % 100;
      datetime /= 100;
      long month = datetime % 100;
      datetime /= 100;
      long year = datetime;
      DateTime dt = new DateTime((int)year, (int)month, (int)day, 0, 0, 0);
      dt = dt.AddHours(hour);
      dt = dt.AddMinutes(min);
      dt = dt.AddSeconds(sec);


      long newDateTime = datetolong(dt);
      if (sec < 0 || sec > 59 ||
          min < 0 || min > 59 ||
          hour < 0 || hour >= 24 ||
          day < 0 || day > 31 ||
          month < 0 || month > 12)
      {
        //Log.WriteFile(LogType.EPG, true, "epg-import:tvguide.xml contains invalid date/time :{0} converted it to:{1}",
        //              orgDateTime, newDateTime);
      }

      return newDateTime;
    }

    public long datetolong(DateTime dt)
    {
      try
      {
        long iSec = 0; //(long)dt.Second;
        long iMin = (long)dt.Minute;
        long iHour = (long)dt.Hour;
        long iDay = (long)dt.Day;
        long iMonth = (long)dt.Month;
        long iYear = (long)dt.Year;

        long lRet = (iYear);
        lRet = lRet * 100L + iMonth;
        lRet = lRet * 100L + iDay;
        lRet = lRet * 100L + iHour;
        lRet = lRet * 100L + iMin;
        lRet = lRet * 100L + iSec;
        return lRet;
      }
      catch (Exception) { }
      return 0;
    }

    public DateTime longtodate(long ldate)
    {
      try
      {
        if (ldate < 0) return DateTime.MinValue;
        int year, month, day, hour, minute, sec;
        sec = (int)(ldate % 100L);
        ldate /= 100L;
        minute = (int)(ldate % 100L);
        ldate /= 100L;
        hour = (int)(ldate % 100L);
        ldate /= 100L;
        day = (int)(ldate % 100L);
        ldate /= 100L;
        month = (int)(ldate % 100L);
        ldate /= 100L;
        year = (int)ldate;
        DateTime dt = new DateTime(year, month, day, hour, minute, 0, 0);
        return dt;
      }
      catch (Exception) { }
      return DateTime.Now;
    }

    public string ConvertHTMLToAnsi(string html)
    {
      string strippedHtml;
      ConvertHTMLToAnsi(html, out strippedHtml);
      return strippedHtml;
    }

    public void ConvertHTMLToAnsi(string html, out string strippedHtml)
    {
      if (html.Length == 0)
      {
        strippedHtml = "";
        return;
      }
      StringWriter writer = new StringWriter();

      System.Web.HttpUtility.HtmlDecode(html, writer);

      String DecodedString = writer.ToString();
      strippedHtml = DecodedString.Replace("<br>", "\n");
      return;
    }

    #region Sort Members

    public int Compare(object x, object y)
    {
      if (x == y) return 0;
      Program item1 = (Program)x;
      Program item2 = (Program)y;
      if (item1 == null) return -1;
      if (item2 == null) return -1;

      if (item1.IdChannel != item2.IdChannel)
      {
        return String.Compare(item1.ReferencedChannel().DisplayName, item2.ReferencedChannel().DisplayName, true);
      }
      if (item1.StartTime > item2.StartTime) return 1;
      if (item1.StartTime < item2.StartTime) return -1;
      return 0;
    }

    #endregion
  }
}