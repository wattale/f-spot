/*
 * FacebookExportDialog.cs
 *
 * Authors:
 *   George Talusan <george@convolve.ca>
 *   Stephane Delcroix <stephane@delcroix.org>
 *   Jim Ramsay <i.am@jimramsay.com>
 *
 * Copyright (C) 2007 George Talusan
 * Copyright (c) 2008-2009 Novell, Inc.
 * Later changes (2009) by Jim Ramsay
 *
 * This is free software. See COPYING for details.
 */

using System;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;

using Gtk;
using GtkBeans;

using FSpot.Utils;
using FSpot.Platform;
using FSpot.UI.Dialog;

using Mono.Facebook;
using Mono.Unix;

namespace FSpot.Exporter.Facebook
{
	internal class FacebookExportDialog : BuilderDialog
	{
		[GtkBeans.Builder.Object] VBox album_info_vbox;
		[GtkBeans.Builder.Object] VBox picture_info_vbox;
		[GtkBeans.Builder.Object] HBox log_buttons_hbox;
		[GtkBeans.Builder.Object] HButtonBox dialog_action_area;
		[GtkBeans.Builder.Object] Button login_button;
		[GtkBeans.Builder.Object] Button logout_button;
		[GtkBeans.Builder.Object] ProgressBar login_progress;
		[GtkBeans.Builder.Object] RadioButton existing_album_radiobutton;
		[GtkBeans.Builder.Object] RadioButton create_album_radiobutton;
		[GtkBeans.Builder.Object] ComboBox existing_album_combobox;
		[GtkBeans.Builder.Object] Table new_album_info_table;
		[GtkBeans.Builder.Object] Entry album_name_entry;
		[GtkBeans.Builder.Object] Entry album_location_entry;
		[GtkBeans.Builder.Object] Entry album_description_entry;
		[GtkBeans.Builder.Object] ScrolledWindow thumbnails_scrolled_window;
		[GtkBeans.Builder.Object] TextView caption_textview;
		[GtkBeans.Builder.Object] TreeView tag_treeview;
		[GtkBeans.Builder.Object] EventBox tag_image_eventbox;
		[GtkBeans.Builder.Object] HBox permissions_hbox;
		[GtkBeans.Builder.Object] CheckButton offline_perm_check;
		[GtkBeans.Builder.Object] CheckButton photo_perm_check;

		Gtk.Image tag_image;
		int tag_image_height;
		int tag_image_width;

		FSpot.Widgets.IconView thumbnail_iconview;
		Dictionary<long, User> friends;

		public FacebookExportDialog (IBrowsableCollection selection) : base (Assembly.GetExecutingAssembly (), "FacebookExport.ui", "facebook_export_dialog")
		{
			items = selection.Items;

			captions = new string [selection.Items.Length];
			tags = new List<Mono.Facebook.Tag> [selection.Items.Length];

			thumbnail_iconview = new FSpot.Widgets.IconView (selection);
			thumbnail_iconview.DisplayDates = false;
			thumbnail_iconview.DisplayTags = false;
			thumbnail_iconview.DisplayRatings = false;
			thumbnail_iconview.ButtonPressEvent += HandleThumbnailIconViewButtonPressEvent;
			thumbnail_iconview.KeyPressEvent += delegate (object sender, KeyPressEventArgs e) {(sender as FSpot.Widgets.IconView).Selection.Clear(); };
			thumbnails_scrolled_window.Add (thumbnail_iconview);
			thumbnail_iconview.Show ();

			login_button.Clicked += HandleLoginClicked;
			logout_button.Clicked += HandleLogoutClicked;
			offline_perm_check.Toggled += HandlePermissionToggled;
			photo_perm_check.Toggled += HandlePermissionToggled;

			create_album_radiobutton.Toggled += HandleCreateAlbumToggled;
			create_album_radiobutton.Active = true;

			existing_album_radiobutton.Toggled += HandleExistingAlbumToggled;

			CellRendererText cell = new CellRendererText ();
			existing_album_combobox.PackStart (cell, true);
			existing_album_combobox.SetAttributes (cell, "text", 0);

			tag_image_eventbox.ButtonPressEvent += HandleTagImageButtonPressEvent;

			tag_treeview.Sensitive = false;
			caption_textview.Sensitive = false;

			DoLogout ();

			account = new FacebookAccount();
			if (account.Authenticated)
				DoLogin ();
		}

		FacebookAccount account;
		public FacebookAccount Account {
			get { return account; }
		}

		string[] captions;
		public string [] Captions {
			get {return captions; } 
		}

		List<Mono.Facebook.Tag>[] tags;
		int current_item;
		IBrowsableItem[] items;
		public IBrowsableItem[] Items {
			get {return items; }
		}

		public bool CreateAlbum {
			get { return create_album_radiobutton.Active; }
		}

		public string AlbumName {
			get { return album_name_entry.Text; }
		}

		public string AlbumLocation {
			get { return album_location_entry.Text; }
		}

		public string AlbumDescription {
			get { return album_description_entry.Text; }
		}
		
		public Album ActiveAlbum {
			get { return ((AlbumStore) existing_album_combobox.Model).Albums [existing_album_combobox.Active]; }
		}

		void HandleThumbnailIconViewButtonPressEvent (object sender, Gtk.ButtonPressEventArgs args)
		{
			int old_item = current_item;
			current_item = thumbnail_iconview.CellAtPosition ((int) args.Event.X, (int) args.Event.Y, false);

			if (current_item < 0 || current_item >=  items.Length) {
				current_item = old_item;
				return;
			}

			captions [old_item] = caption_textview.Buffer.Text;

			string caption = captions [current_item];
			if (caption == null)
				captions [current_item] = caption = "";
			caption_textview.Buffer.Text = caption;
			caption_textview.Sensitive = true;

			tag_treeview.Model = new TagStore (account.Facebook, tags [current_item], friends);

			IBrowsableItem item = items [current_item];

			if (tag_image_eventbox.Children.Length > 0) {
				tag_image_eventbox.Remove (tag_image);
				tag_image.Destroy ();
			}

			using (Gdk.Pixbuf data = PixbufUtils.ScaleToMaxSize (ThumbnailFactory.LoadThumbnail (item.DefaultVersionUri), 400, 400)) {
				tag_image_height = data.Height;
				tag_image_width = data.Width;
				tag_image = new Gtk.Image (data);
				tag_image_eventbox.Add (tag_image);
				tag_image_eventbox.ShowAll ();
			}
		}

		public void HandleLoginClicked (object sender, EventArgs args)
		{
			if (!account.Authenticated) {
				Uri uri = account.GetLoginUri ();
				GtkBeans.Global.ShowUri (Screen, uri.ToString ());

				HigMessageDialog mbox = new HigMessageDialog (this, Gtk.DialogFlags.DestroyWithParent | Gtk.DialogFlags.Modal,
						Gtk.MessageType.Info, Gtk.ButtonsType.Ok, Catalog.GetString ("Waiting for authentication"),
						Catalog.GetString ("F-Spot will now launch your browser so that you can log into Facebook.\n\nOnce you are directed by Facebook to return to this application, click \"Ok\" below.  F-Spot will cache your session in gnome-keyring, if possible, and re-use it on future Facebook exports." ));

				mbox.Run ();
				mbox.Destroy ();

				LoginProgress (0.0, Catalog.GetString ("Authenticating..."));
				account.Authenticate ();
			}
			DoLogin ();
		}

		void DoLogin ()
		{
			if (!account.Authenticated) {
				HigMessageDialog error = new HigMessageDialog (this, Gtk.DialogFlags.DestroyWithParent | Gtk.DialogFlags.Modal,
						Gtk.MessageType.Error, Gtk.ButtonsType.Ok, Catalog.GetString ("Error logging into Facebook"),
						Catalog.GetString ("There was a problem logging into Facebook.  Check your credentials and try again."));
				error.Run ();
				error.Destroy ();

				DoLogout ();
			}
			else {
				log_buttons_hbox.Sensitive = false;
				dialog_action_area.Sensitive = false;
				LoginProgress (0.0, Catalog.GetString ("Authorizing Session"));
				ThreadPool.QueueUserWorkItem (delegate {	
					try {
						bool perm_offline = account.HasPermission("offline_access");
						bool perm_upload = photo_perm_check.Active = account.HasPermission("photo_upload");

						Gtk.Application.Invoke (delegate {
							offline_perm_check.Active = perm_offline;
							photo_perm_check.Active = perm_upload;
							LoginProgress (0.2, Catalog.GetString ("Session established, fetching user info..."));
						});
	
						User me = account.Facebook.GetLoggedInUser ().GetUserInfo ();
	
						Gtk.Application.Invoke (delegate {
							LoginProgress (0.4, Catalog.GetString ("Session established, fetching friend list..."));
						});

						Friend[] friend_list = account.Facebook.GetFriends ();
						long[] uids = new long [friend_list.Length];
	
						for (int i = 0; i < friend_list.Length; i++)
							uids [i] = friend_list [i].UId;
	
						Gtk.Application.Invoke (delegate {
							LoginProgress (0.6, Catalog.GetString ("Session established, fetching friend details..."));
						});

						User[] infos = account.Facebook.GetUserInfo (uids, new string[] { "first_name", "last_name" });
						friends = new Dictionary<long, User> ();
	
						foreach (User user in infos)
							friends.Add (user.UId, user);
	
						Gtk.Application.Invoke (delegate {
							LoginProgress (0.8, Catalog.GetString ("Session established, fetching photo albums..."));
						});
						Album[] albums = account.Facebook.GetAlbums ();
						Gtk.Application.Invoke (delegate {
							existing_album_combobox.Model = new AlbumStore (albums);
							existing_album_combobox.Active = 0;
	
							album_info_vbox.Sensitive = true;
							picture_info_vbox.Sensitive = true;
							permissions_hbox.Sensitive = true;
							login_button.Visible = false;
							logout_button.Visible = true;
							// Note for translators: {0} and {1} are respectively firstname and surname of the user
							LoginProgress (1.0, String.Format (Catalog.GetString ("{0} {1} is logged into Facebook"), me.FirstName, me.LastName));
						});
					} catch (FacebookException fe) {
						Log.DebugException (fe);
						Gtk.Application.Invoke (delegate {
							HigMessageDialog error = new HigMessageDialog (this, Gtk.DialogFlags.DestroyWithParent | Gtk.DialogFlags.Modal,
									Gtk.MessageType.Error, Gtk.ButtonsType.Ok, Catalog.GetString ("Facebook Connection Error"),
									String.Format (Catalog.GetString ("There was an error when downloading your information from Facebook.\n\nFacebook said: {0}"), fe.Message));
							error.Run ();
							error.Destroy ();
						});
	
						account.Deauthenticate ();
						DoLogout ();
					} finally {
						Gtk.Application.Invoke (delegate {
							log_buttons_hbox.Sensitive = true;
							dialog_action_area.Sensitive = true;
						});
					}
				});
			}
		}

		void HandleLogoutClicked (object sender, EventArgs args)
		{
			account.Deauthenticate ();
			DoLogout ();
		}

		void DoLogout ()
		{
			login_button.Visible = true;
			logout_button.Visible = false;

			login_progress.Fraction = 0;
			login_progress.Text = Catalog.GetString ("You are not logged in.");


			album_info_vbox.Sensitive = false;
			picture_info_vbox.Sensitive = false;
			offline_perm_check.Toggled -= HandlePermissionToggled;
			photo_perm_check.Toggled -= HandlePermissionToggled;
			offline_perm_check.Active = false;
			photo_perm_check.Active = false;
			offline_perm_check.Toggled += HandlePermissionToggled;
			photo_perm_check.Toggled += HandlePermissionToggled;
			permissions_hbox.Sensitive = false;
		}

		public void HandlePermissionToggled (object sender, EventArgs args)
		{
			string permission;
			if (sender == offline_perm_check) {
				permission = "offline_access";
			} else if (sender == photo_perm_check) {
				permission = "photo_upload";
			} else {
				throw new Exception ("Unknown Source object");
			}
			CheckButton origin = (CheckButton)sender;
			bool desired = origin.Active;
			bool actual = account.HasPermission (permission);
			if (desired != actual) {
				if (desired) {
					Log.Debug ("Granting {0}", permission);
					account.GrantPermission (permission, this);
				} else {
					Log.Debug ("Revoking {0}", permission);
					account.RevokePermission (permission);
				}
				/* Double-check that things work... */
				actual = account.HasPermission (permission);
				if (actual != desired) {
					Log.Warning("Failed to alter permissions");
				}
				origin.Active = account.HasPermission (permission);
			}
		}

		void HandleCreateAlbumToggled (object sender, EventArgs args)
		{
			if (create_album_radiobutton.Active == false)
				return;

			new_album_info_table.Sensitive = true;
			existing_album_combobox.Sensitive = false;
		}

		void HandleExistingAlbumToggled (object sender, EventArgs args)
		{
			if (existing_album_radiobutton.Active == false)
				return;

			new_album_info_table.Sensitive = false;
			existing_album_combobox.Sensitive = true;
		}

		void HandleTagImageButtonPressEvent (object sender, Gtk.ButtonPressEventArgs args)
		{
			double x = args.Event.X;
			double y = args.Event.Y;

			// translate the centered image to top left corner
			double tag_image_center_x = tag_image_width / 2;
			double tag_image_center_y = tag_image_height / 2;

			double allocation_center_x = tag_image_eventbox.Allocation.Width / 2;
			double allocation_center_y = tag_image_eventbox.Allocation.Height / 2;

			double dx = allocation_center_x - tag_image_center_x;
			double dy = allocation_center_y - tag_image_center_y;

			if (dx < 0)
				dx = 0;
			if (dy < 0)
				dy = 0;

			x -= dx;
			y -= dy;

			// bail if we're in the eventbox but not the image
			if (x < 0 || x > tag_image_width)
				return;
			if (y < 0 || y > tag_image_height)
				return;

			//FacebookTagPopup popup = new FacebookTagPopup (friends);
		}

		void LoginProgress (double percentage, string message)
		{
			login_progress.Fraction = percentage;
			login_progress.Text = message;
			Log.Debug (message);
		}
	}

	internal class AlbumStore : ListStore
	{
		private Album[] _albums;

		public AlbumStore (Album[] albums) : base (typeof (string))
		{
			_albums = albums;

			foreach (Album album in Albums) {
				AppendValues (album.Name);
			}
		}

		public Album[] Albums
		{
			get { return _albums; }
		}
	}

}